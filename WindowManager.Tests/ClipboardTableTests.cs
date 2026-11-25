using System.Text;
using System.Text.RegularExpressions;

namespace WindowManager.Tests;

// What the table editor reads from and writes to the clipboard (ClipboardTable.cs), and the copy / paste session around it
// with the clipboard and the key sender injected, so nothing here touches the real clipboard or sends a key.
public class ClipboardTableTests
{
    static string L(params string[] lines) => string.Join("\n", lines);

    [Fact]
    public void A_tab_separated_block_is_a_table_whose_first_row_is_the_header()
    {
        var t = ClipboardTable.ParseTsv("Name\tQty\r\npear\t10\r\nfig\t100\r\n")!;
        Assert.Equal(new[] { "Name", "Qty" }, t.Rows[0]);
        Assert.Equal(new[] { "fig", "100" }, t.Rows[2]);
        Assert.Equal(3, t.Rows.Count);
    }

    [Fact]
    public void A_tab_separated_block_pads_short_rows_and_escapes_pipes()
    {
        var t = ClipboardTable.ParseTsv("a\tb\tc\nx|y\tz")!;
        Assert.Equal(new[] { "x\\|y", "z", "" }, t.Rows[1]);
    }

    [Fact]
    public void Text_without_tabs_is_not_a_tab_separated_table()
    {
        Assert.Null(ClipboardTable.ParseTsv("just a sentence"));
        Assert.Null(ClipboardTable.ParseTsv("a\tb\nno tab on this line"));
        Assert.Null(ClipboardTable.ParseTsv(""));
    }

    [Fact]
    public void A_markdown_pipe_table_is_parsed_with_its_alignment()
    {
        var t = ClipboardTable.ParsePipe(L("| a | b |", "| :-- | --: |", "| 1 | 2 |"))!;
        Assert.Equal(new[] { "left", "right" }, t.Align);
        Assert.Equal(new[] { "1", "2" }, t.Rows[1]);
    }

    [Fact]
    public void A_pipe_table_inside_other_text_is_found()
    {
        var t = ClipboardTable.ParsePipe(L("intro", "", "| a | b |", "| - | - |", "| 1 | 2 |", "", "outro"))!;
        Assert.Equal(2, t.Rows.Count);
        Assert.Null(ClipboardTable.ParsePipe("no table here"));
    }

    [Fact]
    public void An_html_table_is_parsed_cells_decoded_tags_inside_cells_dropped()
    {
        var html = "<html><body><!--StartFragment--><table><tbody><tr><th>Name</th><th>Qty</th></tr>" +
                   "<tr><td><p>pear &amp; <b>fig</b></p></td><td>1<br>2 | 3</td></tr></tbody></table><!--EndFragment--></body></html>";
        var t = ClipboardTable.ParseHtml(html)!;
        Assert.Equal(new[] { "Name", "Qty" }, t.Rows[0]);
        Assert.Equal(new[] { "pear & fig", "1 2 \\| 3" }, t.Rows[1]);
        Assert.Null(ClipboardTable.ParseHtml("<p>no table</p>"));
    }

    [Fact]
    public void Parse_tries_the_pipe_table_first_then_tabs_then_html_and_gives_nothing_for_plain_text()
    {
        Assert.Equal("p", ClipboardTable.Parse(L("| p |", "| - |", "| 1 |"), "<table><tr><td>h</td></tr></table>")!.Rows[0][0]);
        Assert.Equal("t", ClipboardTable.Parse("t\tu", "<table><tr><td>h</td></tr></table>")!.Rows[0][0]);
        Assert.Equal("h", ClipboardTable.Parse("not tabular", "<table><tr><td>h</td></tr></table>")!.Rows[0][0]);
        Assert.Null(ClipboardTable.Parse("not tabular", null));
        Assert.Null(ClipboardTable.Parse(null, null));
    }

    [Fact]
    public void The_html_table_holds_every_cell_unescaped_and_encoded_the_header_row_in_th()
    {
        var t = MdTable.Parse(L("| a<b | c |", "| - | - |", "| x \\| y | & |"));
        var html = ClipboardTable.ToHtmlTable(t);
        Assert.StartsWith("<table", html);
        Assert.EndsWith("</table>", html);
        Assert.Contains(">a&lt;b</th>", html);
        Assert.Contains(">x | y</td>", html);
        Assert.Contains(">&amp;</td>", html);
        Assert.Equal(2, Regex.Matches(html, "<tr>").Count);
    }

    [Fact]
    public void The_CF_HTML_header_offsets_are_byte_exact_in_UTF_8()
    {
        var cf = ClipboardTable.CfHtml("<table><tr><td>café — 日本</td></tr></table>");
        int Field(string name) => int.Parse(Regex.Match(cf, name + @":(\d+)\r\n").Groups[1].Value);
        var bytes = Encoding.UTF8.GetBytes(cf);
        var startHtml = Field("StartHTML"); var endHtml = Field("EndHTML");
        var startFragment = Field("StartFragment"); var endFragment = Field("EndFragment");
        Assert.StartsWith("Version:0.9\r\n", cf);
        Assert.Equal(bytes.Length, endHtml);
        Assert.StartsWith("<html>", Encoding.UTF8.GetString(bytes, startHtml, endHtml - startHtml));
        Assert.Equal("<table><tr><td>café — 日本</td></tr></table>", Encoding.UTF8.GetString(bytes, startFragment, endFragment - startFragment));
        // StartFragment is just after the start comment, EndFragment just before the end comment
        Assert.EndsWith("<!--StartFragment-->", Encoding.UTF8.GetString(bytes, 0, startFragment));
        Assert.StartsWith("<!--EndFragment-->", Encoding.UTF8.GetString(bytes, endFragment, bytes.Length - endFragment));
    }

    [Fact]
    public void A_CF_HTML_table_can_be_read_back_by_ParseHtml()
    {
        var t = MdTable.Parse(L("| a | b |", "| - | - |", "| 1 | 2 |"));
        var back = ClipboardTable.ParseHtml(ClipboardTable.CfHtml(ClipboardTable.ToHtmlTable(t)))!;
        Assert.Equal(new[] { "a", "b" }, back.Rows[0]);
        Assert.Equal(new[] { "1", "2" }, back.Rows[1]);
    }

    [Theory]
    [InlineData("WINWORD", "Document1 - Word", TargetKind.Html)]
    [InlineData("winword", "", TargetKind.Html)]
    [InlineData("chrome", "x - Google Docs - Chrome", TargetKind.Html)]
    [InlineData("msedge", "Plan - Google Docs - Personal - Microsoft Edge", TargetKind.Html)]
    [InlineData("firefox", "Plan - Google Docs — Mozilla Firefox", TargetKind.Html)]
    [InlineData("Code", "notes.md - VS Code", TargetKind.Text)]
    [InlineData("notepad", "Google Docs.txt - Notepad", TargetKind.Text)]
    [InlineData("chrome", "GitHub - Google Chrome", TargetKind.Text)]
    public void The_target_app_decides_between_an_html_table_and_pipe_text(string process, string title, TargetKind expected)
    {
        Assert.Equal(expected, ClipboardTable.DetectTarget(process, title));
    }

    // ── the session: copy the selection, write the table back, always give the user's clipboard back ──

    sealed class FakeClipboard : ITableClipboard
    {
        public readonly List<string> Events = [];
        public string? Text = "user's own text";
        public string? Html;
        public long Sequence { get; private set; } = 1;
        public string? CopyProduces; // what Ctrl+C puts on the clipboard (null: nothing selected, clipboard untouched)
        public string? CopyProducesHtml;
        public object? Snapshot() { Events.Add("snapshot"); return (Text, Html); }
        public void Restore(object? snapshot) { Events.Add("restore"); (Text, Html) = ((string?, string?))snapshot!; Sequence++; }
        public void Clear() { Events.Add("clear"); Text = null; Html = null; Sequence++; }
        public string? GetText() => Text;
        public string? GetHtml() => Html;
        public void SetTable(string text, string? cfHtml) { Events.Add(cfHtml is null ? "set text" : "set text+html"); Text = text; Html = cfHtml; Sequence++; }
        public bool WaitForChange(long sequence, int timeoutMs) => Sequence != sequence;
        public void Copied() { if (CopyProduces is not null) { Text = CopyProduces; Html = CopyProducesHtml; Sequence++; } }
    }

    sealed class FakeKeys(FakeClipboard clip) : ITableKeys
    {
        public void Copy() { clip.Events.Add("ctrl+c"); clip.Copied(); }
        public void Paste() => clip.Events.Add($"ctrl+v sees [{clip.Text}] html={clip.Html is not null}");
        public bool Focus(nint hwnd) { clip.Events.Add($"focus 0x{hwnd:X}"); return true; }
        public void Backspaces(int n) => clip.Events.Add($"back x{n}");
        public void TypeText(string s) => clip.Events.Add($"type \"{s}\"");
    }

    [Fact]
    public void A_typed_command_is_removed_with_backspaces_and_typed_back_on_cancel()
    {
        var (session, clip) = Make(null);
        session.RemoveTyped("/table");
        session.RestoreTyped("/table");
        Assert.Equal(["back x6", "type \"/table\""], clip.Events); // the clipboard is never touched
    }

    [Fact]
    public void Reading_the_selection_never_clears_the_clipboard_first()
    {
        var (session, clip) = Make("a\tb\n1\t2");
        session.ReadSelection();
        Assert.DoesNotContain("clear", clip.Events); // clearing made this thread the clipboard owner and blocked the target's copy
    }

    static (TableSession Session, FakeClipboard Clip) Make(string? copied, string? copiedHtml = null)
    {
        var clip = new FakeClipboard { CopyProduces = copied, CopyProducesHtml = copiedHtml };
        return (new TableSession(clip, new FakeKeys(clip), ms => clip.Events.Add($"wait {ms}")), clip);
    }

    [Fact]
    public void Reading_the_selection_copies_it_parses_it_and_gives_the_users_clipboard_back()
    {
        var (session, clip) = Make("a\tb\n1\t2");
        var t = session.ReadSelection();
        Assert.Equal(new[] { "a", "b" }, t.Rows[0]);
        Assert.Equal("user's own text", clip.Text);
        Assert.Equal(new[] { "snapshot", "ctrl+c", "restore" }, clip.Events);
    }

    [Fact]
    public void Reading_with_nothing_selected_gives_a_new_3x3_table_with_a_header_row()
    {
        var (session, clip) = Make(null);
        var t = session.ReadSelection();
        Assert.Equal(4, t.Rows.Count);
        Assert.All(t.Rows, r => Assert.Equal(3, r.Count));
        Assert.Equal(new[] { "Column 1", "Column 2", "Column 3" }, t.Rows[0]);
        Assert.Equal("user's own text", clip.Text);
    }

    [Fact]
    public void Reading_a_selection_that_is_text_without_a_table_but_has_html_with_a_table_uses_the_html()
    {
        var (session, _) = Make("not tabular", ClipboardTable.CfHtml("<table><tr><td>h1</td><td>h2</td></tr></table>"));
        Assert.Equal(new[] { "h1", "h2" }, session.ReadSelection().Rows[0]);
    }

    [Fact]
    public void Writing_for_Word_puts_html_and_the_pipe_text_on_the_clipboard_focuses_the_window_pastes_then_restores()
    {
        var (session, clip) = Make(null);
        var t = MdTable.Parse(L("| a | b |", "| - | - |", "| 1 | 2 |"));
        session.WriteBack(t, new TableTarget(0xAB, "WINWORD", "Document1 - Word"));
        Assert.Equal(new[] { "snapshot", "set text+html", "focus 0xAB", "wait 150", "ctrl+v sees [" + MdTable.Format(t) + "] html=True", "wait 500", "restore" }, clip.Events);
        Assert.Equal("user's own text", clip.Text);
        Assert.Null(clip.Html);
    }

    [Fact]
    public void Writing_for_a_text_target_puts_only_the_formatted_pipe_table_on_the_clipboard()
    {
        var (session, clip) = Make(null);
        var t = MdTable.Parse(L("| a | b |", "| - | - |", "| 1 | 2 |"));
        session.WriteBack(t, new TableTarget(0xCD, "Code", "notes.md - VS Code"));
        Assert.Contains("set text", clip.Events);
        Assert.DoesNotContain("set text+html", clip.Events);
        Assert.Contains("ctrl+v sees [" + MdTable.Format(t) + "] html=False", clip.Events);
        Assert.Equal("restore", clip.Events[^1]);
    }

    [Fact]
    public void The_users_clipboard_comes_back_even_when_the_paste_throws()
    {
        var clip = new FakeClipboard();
        var session = new TableSession(clip, new ThrowingKeys(), _ => { });
        Assert.Throws<InvalidOperationException>(() => session.WriteBack(MdTable.Parse(MdTable.Create(1, 2)), new TableTarget(1, "notepad", "x")));
        Assert.Equal("user's own text", clip.Text);
    }

    sealed class ThrowingKeys : ITableKeys
    {
        public void Copy() { }
        public void Paste() => throw new InvalidOperationException("no input");
        public bool Focus(nint hwnd) => true;
        public void Backspaces(int n) { }
        public void TypeText(string s) { }
    }
}
