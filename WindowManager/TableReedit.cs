using System.Text.RegularExpressions;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace WindowManager;

// Going back to a table already in a document: the system-wide building blocks. TableAround is the pure part (MdTable.Find, as a range);
// Read looks through UI Automation at the text control that has the focus and returns the pipe table the caret is in, its text and
// a selectable range, where the caret is in that text, and where it is on screen.
// Wiring (when to read, what a click does) is the caller's.
// Top: the table's first line on screen (its left edge is where the table's text starts).
public sealed record TableAtCaret(string Text, Rectangle Caret, TextPatternRange Range, int CaretOffset, Rectangle Top);

public static class TableReedit
{
    const int MaxLines = 200; // how far the table is followed up and down from the caret line
    static readonly Regex TableLine = new(@"^ {0,3}\|", RegexOptions.Compiled);
    static readonly HashSet<string> NoTextPatternLogged = [];

    // ── pure ──

    // The character range [Start, End) of the whole pipe table the caret is on (all its lines, no trailing newline), or null. `text` uses "\n".
    public static (int Start, int End)? TableAround(string text, int caret) =>
        MdTable.Find(text, caret) is { } f ? (f.Start, f.End) : null;

    // The original's G.off: leaving the grid by keyboard (Esc, an arrow past its edge, Edit as text) keeps that table closed until the caret has
    // been outside it. The caret is left at the table's end, so without this a Ctrl+End / End / arrow right after Esc reopened it. Muted: the
    // table as written (the final text), not as it was opened. A click-away is not muted: the click may be the way back in.
    public static string? MuteAfterClose(PopupResult r)
    {
        if (!r.AsText && r.Reason is not ("escape" or "left")) return null;
        var text = r.Text.Trim('\n');
        return MdTable.Find(text, 0) is { } f ? text[f.Start..f.End] : null;
    }

    // A line that can be part of a pipe table: after up to three spaces it starts with "|".
    public static bool LooksLikeTableLine(string line) => TableLine.IsMatch(line);

    // ── UI Automation ──

    // The table the caret is in, in the focused text control (or `focused`), or null: not a text control, the caret's line is not a
    // table line (the hot path: nothing more is asked of the app), or UIA failed. Never throws. Text has "\n" line breaks.
    // The Range spans exactly the table's text: from its first "|" line to the end of its last line, WITHOUT the trailing line break
    // (it falls back to including the break only if the control does not let the end move back by a character).
    // Lines are UIA lines, as the control lays them out: a table row wrapped over two screen lines ends the table there.
    public static TableAtCaret? Read(AutomationElement? focused = null)
    {
        try
        {
            var el = focused ?? AutomationElement.FocusedElement;
            if (el is null) return null;
            if (!el.TryGetCurrentPattern(TextPattern.Pattern, out var obj) || obj is not TextPattern pattern)
            {
                var cls = el.Current.ClassName;
                lock (NoTextPatternLogged)
                    if (NoTextPatternLogged.Add(cls)) Log.Info($"table reedit: {(cls == "" ? "(no class)" : cls)} has no TextPattern");
                return null;
            }
            var selection = pattern.GetSelection();
            if (selection.Length == 0) return null;

            // The caret: the start of the selection, as an empty range.
            var caret = selection[0].Clone();
            caret.MoveEndpointByRange(TextPatternRangeEndpoint.End, caret, TextPatternRangeEndpoint.Start);
            var line = caret.Clone();
            line.ExpandToEnclosingUnit(TextUnit.Line);
            if (Normalize(line.GetText(-1)).Trim() == "")   // the caret at the very end of the text: some controls give an empty "line" there (measured: a WinForms text box)
            {
                var back = caret.Clone();
                if (back.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -1) != 0) { back.ExpandToEnclosingUnit(TextUnit.Line); line = back; }
            }
            if (!LooksLikeTableLine(Normalize(line.GetText(-1)).TrimEnd('\n'))) return null;

            // Grow line by line while the neighbouring lines look like table lines.
            var range = line.Clone();
            for (var i = 0; i < MaxLines; i++)
            {
                var up = range.Clone();
                if (up.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Line, -1) == 0) break;
                if (!LooksLikeTableLine(Normalize(up.GetText(-1)).Split('\n')[0])) break;
                range = up;
            }
            for (var i = 0; i < MaxLines; i++)
            {
                var down = range.Clone();
                if (down.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Line, 1) == 0) break;
                var lines = Normalize(down.GetText(-1)).TrimEnd('\n').Split('\n');
                if (!LooksLikeTableLine(lines[^1]) || lines.Length == Normalize(range.GetText(-1)).TrimEnd('\n').Split('\n').Length) break;
                range = down;
            }

            // Where the caret is in that text, and whether it really is a table (header + separator, not in a code fence).
            var text = Normalize(range.GetText(-1));
            var before = range.Clone();
            before.MoveEndpointByRange(TextPatternRangeEndpoint.End, caret, TextPatternRangeEndpoint.Start);
            var offset = Math.Min(Normalize(before.GetText(-1)).Length, text.Length);
            var body = text.TrimEnd('\n');
            if (TableAround(body, Math.Min(offset, body.Length)) is not var (s, e)) { Log.Info($"table reedit: table lines round the caret but no table (caret {offset} of {body.Length}): \"{Clip(body)}\""); return null; }

            // Narrow to whole lines when the table is only part of the run (a fence or a lone "|" line at the edge).
            if (s != 0 || e != body.Length)
            {
                var top = body[..s].Count(c => c == '\n');
                var bottom = body[e..].Count(c => c == '\n');
                if (bottom > 0) { Log.Info("table reedit: a shorter table at the end of a longer run of table lines, left alone"); return null; }
                if (top > 0 && range.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Line, top) != top) return null;
                if (Normalize(range.GetText(-1)).TrimEnd('\n') != body[s..e]) { Log.Info("table reedit: the narrowed range does not match the table, left alone"); return null; }
            }

            TrimTrailingBreak(range);
            var table = Normalize(range.GetText(-1)).TrimEnd('\n');
            var rect = CaretRectangle(caret, line);
            if (rect is null) { Log.Info("table reedit: the caret has no bounding rectangle"); return null; }
            var firstLine = First(range) ?? rect.Value;
            return new TableAtCaret(table, rect.Value, range, Math.Clamp(offset - s, 0, table.Length), firstLine);
        }
        catch (Exception ex)
        {
            Log.Info($"table reedit: read failed ({ex.GetType().Name}: {ex.Message})");
            return null;
        }
    }

    // Select the table in its app (the next typed character replaces it). False when the app refuses.
    // Checked: the selection must be exactly the table. The range's end can be off by the line break's characters (\r\n as one or two, the
    // trailing-break trim): a selection a few characters short left the end of the last row behind after the first write (measured).
    public static bool Select(TableAtCaret t) => Select(t, out _);

    public static bool Select(TableAtCaret t, out bool exact)
    {
        exact = false;
        try
        {
            var pattern = (TextPattern)AutomationElement.FocusedElement.GetCurrentPattern(TextPattern.Pattern);
            foreach (var adjust in new[] { 0, 1, 2, -1, 3, 4, -2 })
            {
                var r = t.Range.Clone();
                if (adjust != 0 && r.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, adjust) != adjust) continue;
                if (Normalize(r.GetText(-1)).TrimEnd('\n') != t.Text) continue;
                r.Select();
                if (pattern.GetSelection() is { Length: > 0 } s && Normalize(s[0].GetText(-1)).TrimEnd('\n') == t.Text) return true;
            }
            exact = false;
            Log.Info("table reedit: the selection could not be made exactly the table through UIA");
            return true;
        }
        catch (Exception ex) { Log.Info($"table reedit: select failed ({ex.GetType().Name}: {ex.Message})"); exact = false; return false; }
    }

    // The caret just after the table's last character (the live write's select-back then selects it, checked).
    public static bool PlaceCaretAtEnd(TableAtCaret t)
    {
        try { var r = t.Range.Clone(); r.MoveEndpointByRange(TextPatternRangeEndpoint.Start, r, TextPatternRangeEndpoint.End); r.Select(); return true; }
        catch { return false; }
    }

    // The caret at the table's first character (for selecting it with keys when the UIA selection cannot be made exact).
    public static bool PlaceCaretAtStart(TableAtCaret t)
    {
        try { var r = t.Range.Clone(); r.MoveEndpointByRange(TextPatternRangeEndpoint.End, r, TextPatternRangeEndpoint.Start); r.Select(); return true; }
        catch { return false; }
    }

    static string Clip(string s) => (s.Length > 160 ? s[..160] + "…" : s).Replace("\n", "\\n");

    static string Normalize(string s) => s.Replace("\r\n", "\n").Replace('\r', '\n');

    // Moves the end of a range ending in a line break back before it. The break may count as 1 or 2 characters, so try both and keep
    // the one that leaves the text equal to the old text minus the break; otherwise leave the range as it is.
    static void TrimTrailingBreak(TextPatternRange range)
    {
        var raw = range.GetText(-1);
        var n = raw.Length - raw.TrimEnd('\r', '\n').Length;
        if (n == 0) return;
        var want = raw[..^n];
        foreach (var steps in new[] { 1, n }.Distinct())
        {
            var probe = range.Clone();
            if (probe.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, -steps) != -steps) continue;
            if (probe.GetText(-1) != want) continue;
            range.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, -steps);
            return;
        }
    }

    // The caret in screen pixels: the rectangle of the empty range, else of the next (or previous) character, else the caret's line.
    static Rectangle? CaretRectangle(TextPatternRange caret, TextPatternRange line)
    {
        if (First(caret) is { } r) return r;
        var next = caret.Clone();
        if (next.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, 1) != 0 && First(next) is { } n) return new Rectangle(n.X, n.Y, 0, n.Height);
        var prev = caret.Clone();
        if (prev.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -1) != 0 && First(prev) is { } p) return new Rectangle(p.Right, p.Y, 0, p.Height);
        return First(line);
    }

    static Rectangle? First(TextPatternRange r)
    {
        foreach (var rect in r.GetBoundingRectangles())
            if (!rect.IsEmpty && double.IsFinite(rect.X) && double.IsFinite(rect.Width) && rect.Height > 0)
                return new Rectangle((int)Math.Round(rect.X), (int)Math.Round(rect.Y), (int)Math.Round(rect.Width), (int)Math.Round(rect.Height));
        return null;
    }
}
