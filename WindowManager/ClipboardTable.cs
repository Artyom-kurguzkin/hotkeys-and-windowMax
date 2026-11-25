using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace WindowManager;

// How the table editor (Win+Alt+T) gets a table out of the focused control and puts it back. Pure functions here, plus TableSession:
// the copy / paste dance with the clipboard and the key sender injected, so the order of events is a test and nothing touches the real
// clipboard in one. The real clipboard is WinClipboard, the real keys are in Program.Table.cs.

public enum TargetKind { Text, Html }

// The window that had the focus when the hotkey was pressed (taken before the editor opened).
public readonly record struct TableTarget(nint Hwnd, string Process, string Title)
{
    public TargetKind Kind => ClipboardTable.DetectTarget(Process, Title);
}

public interface ITableClipboard
{
    long Sequence { get; }
    object? Snapshot();             // everything on the clipboard now, to give back with Restore
    void Restore(object? snapshot); // null: the clipboard was empty
    void Clear();
    string? GetText();
    string? GetHtml();              // the CF_HTML string, framing included
    void SetTable(string text, string? cfHtml);
    bool WaitForChange(long sequence, int timeoutMs); // true when the clipboard changed from `sequence`
}

public interface ITableKeys
{
    void Copy();            // Ctrl+C into the focused control
    void Paste();           // Ctrl+V
    bool Focus(nint hwnd);  // bring the target window back to the front
    bool IsForeground(nint hwnd) => true; // the target has the focus now (the fakes in tests always say yes)
    void Backspaces(int n); // delete the last n typed characters
    void TypeText(string s); // type text again (Unicode key events)
}

public static class ClipboardTable
{
    static readonly string[] Browsers = ["chrome", "msedge", "firefox", "vivaldi", "brave", "opera", "arc", "chromium", "iexplore"];

    // Word and Google Docs take a pasted HTML table as a real table; everything else (markdown, txt, code editors) gets pipe text.
    public static TargetKind DetectTarget(string process, string title)
    {
        if (process.Equals("WINWORD", StringComparison.OrdinalIgnoreCase)) return TargetKind.Html;
        if (Browsers.Contains(process, StringComparer.OrdinalIgnoreCase) && title.Contains("Google Docs", StringComparison.OrdinalIgnoreCase)) return TargetKind.Html;
        return TargetKind.Text;
    }

    // ── reading ──

    // A markdown pipe table anywhere in the text (the first one).
    public static PipeTable? ParsePipe(string text)
    {
        var at = 0;
        foreach (var line in text.Replace("\r", "").Split('\n'))
        {
            if (line.TrimStart().StartsWith('|') && MdTable.Find(text.Replace("\r", ""), at) is { } f) return MdTable.Parse(f.Text);
            at += line.Length + 1;
        }
        return null;
    }

    // What Word and Google Docs put in the text format when a table is copied: cells split by tabs, rows by line breaks.
    // Every non-empty line must have a tab, or it is not tabular. The first row becomes the header.
    public static PipeTable? ParseTsv(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Where(l => l != "").ToList();
        if (lines.Count == 0 || lines.Any(l => !l.Contains('\t'))) return null;
        return FromRows(lines.Select(l => l.Split('\t').AsEnumerable()));
    }

    // The first <table> of an HTML string (CF_HTML framing or not): rows by <tr>, cells by <td> / <th>.
    public static PipeTable? ParseHtml(string html)
    {
        var table = Regex.Match(html, @"<table\b.*?</table>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (!table.Success) return null;
        var rows = new List<IEnumerable<string>>();
        foreach (Match tr in Regex.Matches(table.Value, @"<tr\b[^>]*>(.*?)</tr>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            var cells = Regex.Matches(tr.Groups[1].Value, @"<t[dh]\b[^>]*>(.*?)</t[dh]>", RegexOptions.Singleline | RegexOptions.IgnoreCase)
                .Select(c => CellText(c.Groups[1].Value)).ToList();
            if (cells.Count > 0) rows.Add(cells);
        }
        return rows.Count == 0 ? null : FromRows(rows);
    }

    static string CellText(string html)
    {
        var s = Regex.Replace(html, @"<br\s*/?>|</p>|</div>", " ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<[^>]*>", "");
        return Regex.Replace(WebUtility.HtmlDecode(s), @"\s+", " ").Trim();
    }

    static PipeTable FromRows(IEnumerable<IEnumerable<string>> rows)
    {
        var list = rows.Select(r => r.ToList()).ToList();
        var cols = Math.Max(1, list.Max(r => r.Count));
        // Build through the pipe model so cells are escaped the same way an edit escapes them.
        var t = new PipeTable { Rows = [.. list.Select(_ => Enumerable.Repeat("", cols).ToList())], Align = Enumerable.Repeat("", cols).ToList() };
        for (var r = 0; r < list.Count; r++)
            for (var c = 0; c < list[r].Count; c++) t = MdTable.SetCell(t, r, c, list[r][c]);
        return t;
    }

    // The selection as a table, in the order the editor trusts: a pipe table, then tabs, then an HTML table; null when none.
    public static PipeTable? Parse(string? text, string? html)
    {
        if (!string.IsNullOrWhiteSpace(text) && (ParsePipe(text) ?? ParseTsv(text)) is { } t) return t;
        return string.IsNullOrWhiteSpace(html) ? null : ParseHtml(html);
    }

    // ── writing ──

    public static string ToHtmlTable(PipeTable t)
    {
        var sb = new StringBuilder("<table border=\"1\" cellspacing=\"0\" cellpadding=\"4\" style=\"border-collapse:collapse\"><tbody>");
        for (var r = 0; r < t.Rows.Count; r++)
        {
            sb.Append("<tr>");
            var tag = r == 0 ? "th" : "td";
            for (var c = 0; c < t.Rows[r].Count; c++)
            {
                var align = t.Align[c] == "" ? "" : $"text-align:{t.Align[c]};";
                sb.Append($"<{tag} style=\"border:1px solid #000;padding:4px;{align}\">{WebUtility.HtmlEncode(MdTable.Unescape(t.Rows[r][c]))}</{tag}>");
            }
            sb.Append("</tr>");
        }
        return sb.Append("</tbody></table>").ToString();
    }

    // The CF_HTML clipboard string ("HTML Format", UTF-8): a header of byte offsets, then a document whose fragment sits between the
    // verbatim comments. StartFragment is just after the start comment, EndFragment just before the end comment. The header has
    // fixed-width (ten digit) numbers, so its own length does not depend on them.
    public static string CfHtml(string fragment)
    {
        const string head = "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
        const string before = "<html>\r\n<body>\r\n<!--StartFragment-->", after = "<!--EndFragment-->\r\n</body>\r\n</html>";
        int Bytes(string s) => Encoding.UTF8.GetByteCount(s);
        var startHtml = Bytes(string.Format(head, 0, 0, 0, 0));
        var startFragment = startHtml + Bytes(before);
        var endFragment = startFragment + Bytes(fragment);
        var endHtml = endFragment + Bytes(after);
        return string.Format(head, startHtml, endHtml, startFragment, endFragment) + before + fragment + after;
    }
}

// Copies the selection out of the focused control and writes the edited table back, always giving the user's own clipboard back.
public sealed class TableSession(ITableClipboard clip, ITableKeys keys, Action<int> sleep)
{
    public const int CopyTimeoutMs = 600, FocusSettleMs = 150, PasteSettleMs = 500;

    // Ctrl+C into the focused control, then parse what arrived; a new 3x3 table when nothing was selected or it holds no table.
    public PipeTable ReadSelection()
    {
        var (text, html) = CopySelection();
        return ClipboardTable.Parse(text, html) ?? MdTable.Parse(MdTable.Create(3, 3));
    }

    // The keystroke watcher saw `command` typed: delete it the way espanso removes a trigger (backspaces), and put it back on cancel.
    // Nothing is copied to check it: a copy from the target blocked for seconds on a real machine (its WM_DESTROYCLIPBOARD went to
    // our non-pumping clipboard owner thread), so the check is the typed characters plus the focused control's UIA type (Program.Table.cs).
    public void RemoveTyped(string command) => keys.Backspaces(command.Length);
    public void RestoreTyped(string command) => keys.TypeText(command);

    // No Clear() before the copy: owning the clipboard from this thread is what made the target's copy block (see RemoveTyped);
    // "nothing selected" still shows as an unchanged sequence number.
    (string? Text, string? Html) CopySelection()
    {
        var snapshot = clip.Snapshot();
        try
        {
            var seq = clip.Sequence;
            keys.Copy();
            if (!clip.WaitForChange(seq, CopyTimeoutMs)) return (null, null); // nothing copied: nothing selected
            string? text = null;
            for (var i = 0; i < 4 && (text = clip.GetText()) is null; i++) sleep(50); // the copier or the clipboard history may still hold the clipboard open
            return (text, clip.GetHtml());
        }
        finally { clip.Restore(snapshot); }
    }

    // The table over the selection of the target: HTML + pipe text for Word and Google Docs, pipe text for the rest; focus the window, paste.
    // kind: how to write it (default: the target's); a pipe table already in a Word document is re-written as text, not turned into a Word table.
    // The live writes save the user's clipboard once when the editor opens and give it back once when it closes, not around every write
    // (a save and restore cost ~0.4 s each time, measured).
    public object? SaveClipboard() => clip.Snapshot();
    public void RestoreClipboard(object? snapshot) => clip.Restore(snapshot);

    public void WriteBack(PipeTable table, TableTarget target, TargetKind? kind = null) => WriteText(MdTable.Format(table), target, kind);

    // Text from the editor's textarea over the target's selection (or at its caret). As HTML too only when the text is exactly one
    // table and the target takes HTML tables (a table plus other lines stays text: Word would get half a table and half prose).
    // focusSettleMs / pasteSettleMs: shorter for the live writes while the editor is open (they happen after every change).
    public void WriteText(string text, TableTarget target, TargetKind? kind = null, int focusSettleMs = FocusSettleMs, int pasteSettleMs = PasteSettleMs, bool restoreClipboard = true)
    {
        var html = (kind ?? target.Kind) == TargetKind.Html && MdTable.Find(text, 0) is { } f && f.Start == 0 && f.End == text.Length
            ? ClipboardTable.CfHtml(ClipboardTable.ToHtmlTable(MdTable.Parse(text))) : null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var snapshot = restoreClipboard ? clip.Snapshot() : null; var keep = !restoreClipboard;
        try
        {
            clip.SetTable(text, html);
            var tSet = sw.ElapsedMilliseconds;
            // Paste only into the target: another window that took the focus meanwhile (measured: a topmost window popping up while
            // the editor was open) got the table instead. Retried; if the target never comes back, nothing is pasted.
            var focused = false;
            for (var i = 0; i < 4 && !focused; i++)
            {
                keys.Focus(target.Hwnd);
                sleep(focusSettleMs);   // the window needs a moment to take the focus before the paste goes to it
                focused = keys.IsForeground(target.Hwnd);
            }
            if (sw.ElapsedMilliseconds > 300) Log.Info($"table editor: slow write: snapshot+clipboard set {tSet}ms, focus {sw.ElapsedMilliseconds - tSet}ms ({(focused ? "ok" : "refused")})");
            if (!focused) { Log.Info("table editor: the target never took the focus back, nothing pasted (the table is left on the clipboard)"); keep = true; return; }
            keys.Paste();
            sleep(pasteSettleMs);   // the target reads the clipboard when it handles the paste, a little after the key
        }
        finally { if (!keep) clip.Restore(snapshot); }
    }
}

// The real clipboard (WinForms; call from an STA thread). The snapshot copies every format that can be read; a format that cannot
// (delayed rendering by a program that has gone) is skipped.
public sealed class WinClipboard : ITableClipboard
{
    [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();

    public long Sequence => GetClipboardSequenceNumber();

    // Only the formats a user would miss. Copying every format and putting them all back failed every time on a real clipboard
    // ("Requested Clipboard operation did not succeed"): an app-private format (EnterpriseDataProtectionId) came back as an object
    // the flush could not render, and the whole restore was refused.
    static readonly string[] Kept = [DataFormats.UnicodeText, DataFormats.Text, DataFormats.Html, DataFormats.Rtf, DataFormats.CommaSeparatedValue, DataFormats.FileDrop, DataFormats.Bitmap];

    public object? Snapshot()
    {
        try
        {
            if (Clipboard.GetDataObject() is not { } live) return null;
            var copy = new DataObject();
            foreach (var format in Kept)
            {
                try { if (live.GetDataPresent(format, false) && live.GetData(format, false) is { } data) copy.SetData(format, false, data); } catch { /* skip an unreadable format */ }
            }
            return copy.GetFormats().Length == 0 ? null : copy;
        }
        catch (Exception e) { Log.Info($"table editor: clipboard snapshot failed: {Hr(e)}"); return null; }
    }

    public void Restore(object? snapshot)
    {
        try
        {
            if (snapshot is DataObject data) Clipboard.SetDataObject(data, true, 5, 50);
            else Clipboard.Clear();
        }
        catch (Exception e) { Log.Info($"table editor: clipboard restore failed: {Hr(e)}"); }
    }

    public void Clear() { try { Clipboard.Clear(); } catch (Exception e) { Log.Info($"table editor: clipboard clear failed: {Hr(e)}"); } }

    public string? GetText() { try { return Clipboard.ContainsText() ? Clipboard.GetText(TextDataFormat.UnicodeText) : null; } catch (Exception e) { Log.Info($"table editor: clipboard read failed: {Hr(e)}"); return null; } }

    public string? GetHtml() { try { return Clipboard.ContainsText(TextDataFormat.Html) ? Clipboard.GetText(TextDataFormat.Html) : null; } catch (Exception e) { Log.Info($"table editor: clipboard html read failed: {Hr(e)}"); return null; } }

    static string Hr(Exception e) => $"{e.GetType().Name}: {e.Message}{(e is ExternalException x ? $" (hr 0x{x.ErrorCode:X8})" : "")}";

    public void SetTable(string text, string? cfHtml)
    {
        var data = new DataObject();
        data.SetText(text, TextDataFormat.UnicodeText);
        if (cfHtml is not null) data.SetText(cfHtml, TextDataFormat.Html); // the string is already in CF_HTML framing; WinForms writes it as UTF-8
        Clipboard.SetDataObject(data, true, 5, 50);
    }

    public bool WaitForChange(long sequence, int timeoutMs)
    {
        for (var waited = 0; waited < timeoutMs; waited += 10)
        {
            if (GetClipboardSequenceNumber() != sequence) { Thread.Sleep(20); return true; } // let the copier finish writing its formats
            Thread.Sleep(10);
        }
        return GetClipboardSequenceNumber() != sequence;
    }
}
