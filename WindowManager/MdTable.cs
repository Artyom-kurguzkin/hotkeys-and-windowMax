using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace WindowManager;

// Markdown pipe tables as pure functions (no UI): a C# port of repo-task-manager's md-table.js, tests in MdTableTests.
// A table in a document is a run of lines that start with "|", whose second line is the separator row (| --- | :-: |);
// lines inside code fences do not count. The model: rows[0] is the header, rows[1..] the body; the separator row is `Align`
// ("" | "left" | "center" | "right" per column). Documents are strings, carets are offsets; a cell's width is its length in characters.
// Cells are stored escaped (a literal pipe is "\|"); Unescape gives the text to show.

public sealed class PipeTable
{
    public List<List<string>> Rows { get; set; } = [];
    public List<string> Align { get; init; } = [];
    public PipeTable Copy() => new() { Rows = [.. Rows.Select(r => new List<string>(r))], Align = [.. Align] };
}

public readonly record struct Sel(int R0, int C0, int R1, int C1);
public sealed record OpResult(PipeTable Table, Sel Sel);
// A change to a document: the whole new text, the selection to put afterwards, and the replaced range (what to type over what).
public sealed record TextEdit(string Text, int Start, int End, int RangeStart, int RangeEnd, string Replacement);
public sealed record Found(int StartLine, int EndLine, int Start, int End, string Text, int Row, int Col, int Line);
// Where a navigation key leads: a cell, or (Leave set) out of the table "before" / "after" it.
public readonly record struct Nav(int R, int C, string? Leave = null);
public readonly record struct CellSpan(string Text, int From, int To, int Start, int End);
public readonly record struct RibbonButton(string Op, string Label, string Title);

public static class MdTable
{
    static readonly Regex SepCell = new(@"^:?-+:?$", RegexOptions.Compiled);
    static readonly Regex Fence = new(@"^ {0,3}(`{3,}|~{3,})", RegexOptions.Compiled);

    static int Width(string s) => s.EnumerateRunes().Count();

    // The last "\n" at or before `from` (a negative start looks at index 0 only, like a JavaScript lastIndexOf).
    static int LastNewline(string doc, int from)
    {
        if (doc.Length == 0) return -1;
        return doc.LastIndexOf('\n', Math.Clamp(from, 0, doc.Length - 1));
    }
    static string Repeat(char c, int n) => n <= 0 ? "" : new string(c, n);

    // The cells of one line: text (trimmed), Start/End (the trimmed text in the line), From/To (between the pipes). An empty cell has
    // Start = End, just after the pipe and its space. A pipe after a backslash or inside a code span is not a divider.
    public static List<CellSpan> SplitRow(string line)
    {
        var cells = new List<CellSpan>();
        var i = 0;
        while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
        if (i < line.Length && line[i] == '|') i++;
        var from = i;
        void Push(int to)
        {
            int s = from, e = to;
            while (s < e && char.IsWhiteSpace(line[s])) s++;
            while (e > s && char.IsWhiteSpace(line[e - 1])) e--;
            if (s == e) s = e = Math.Min(from + 1, to);
            cells.Add(new CellSpan(line[s..e], from, to, s, e));
        }
        for (var k = i; k < line.Length; k++)
        {
            var c = line[k];
            if (c == '\\') { k++; continue; }
            if (c == '`')
            {
                var n = 1;
                while (k + n < line.Length && line[k + n] == '`') n++;
                var close = line.IndexOf(new string('`', n), k + n, StringComparison.Ordinal);
                k = close >= 0 ? close + n - 1 : k + n - 1;
                continue;
            }
            if (c == '|') { Push(k); from = k + 1; }
        }
        if (from <= line.Length && line[Math.Min(from, line.Length)..].Trim() != "") Push(line.Length);
        return cells;
    }

    static bool IsSeparator(string line)
    {
        var c = SplitRow(line);
        return c.Count > 0 && c.All(x => SepCell.IsMatch(x.Text));
    }

    static string AlignOf(string cell)
    {
        bool l = cell.StartsWith(':'), r = cell.EndsWith(':');
        return l && r ? "center" : l ? "left" : r ? "right" : "";
    }

    static List<string> Pad(IEnumerable<string> cells, int cols)
    {
        var r = Enumerable.Repeat("", cols).ToList();
        var i = 0;
        foreach (var c in cells) r[i++] = c;
        return r;
    }

    public static PipeTable Parse(string text)
    {
        var lines = text.Replace("\r", "").Split('\n');
        var head = SplitRow(lines[0]).Select(c => c.Text).ToList();
        var sep = lines.Length > 1 ? SplitRow(lines[1]).Select(c => AlignOf(c.Text)).ToList() : [];
        var body = lines.Skip(2).Select(l => SplitRow(l).Select(c => c.Text).ToList()).ToList();
        var cols = new[] { 1, head.Count, sep.Count }.Concat(body.Select(r => r.Count)).Max();
        return new PipeTable { Rows = [Pad(head, cols), .. body.Select(r => Pad(r, cols))], Align = Pad(sep, cols) };
    }

    public static string Format(PipeTable t)
    {
        var cols = t.Rows[0].Count;
        var w = Enumerable.Range(0, cols).Select(c =>
            Math.Max(3 + (t.Align[c] == "center" ? 2 : t.Align[c] != "" ? 1 : 0), t.Rows.Max(r => Width(r[c])))).ToArray();
        string Cell(string s, int c)
        {
            var room = w[c] - Width(s);
            if (t.Align[c] == "right") return Repeat(' ', room) + s;
            if (t.Align[c] == "center") return Repeat(' ', room / 2) + s + Repeat(' ', room - room / 2);
            return s + Repeat(' ', room);
        }
        string Line(List<string> r) => "| " + string.Join(" | ", r.Select(Cell)) + " |";
        string Dashes(int c) => t.Align[c] switch
        {
            "center" => ":" + Repeat('-', w[c] - 2) + ":",
            "left" => ":" + Repeat('-', w[c] - 1),
            "right" => Repeat('-', w[c] - 1) + ":",
            _ => Repeat('-', w[c]),
        };
        return string.Join("\n", new[] { Line(t.Rows[0]), "| " + string.Join(" | ", Enumerable.Range(0, cols).Select(Dashes)) + " |" }
            .Concat(t.Rows.Skip(1).Select(Line)));
    }

    public static PipeTable InsertRow(PipeTable t, int r, bool below)
    {
        if (r == 0 && !below) return t;
        var n = t.Copy();
        n.Rows.Insert(below ? r + 1 : r, Enumerable.Repeat("", t.Rows[0].Count).ToList());
        return n;
    }

    // The header of a new column: "Column N", N one more than the highest "Column <number>" in the header row, else the column count + 1.
    static string FreshHeader(List<string> header)
    {
        var nums = header.Select(h => Regex.Match(h, @"^Column (\d+)$")).Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Value)).ToList();
        return "Column " + (nums.Count > 0 ? nums.Max() + 1 : header.Count + 1);
    }

    public static PipeTable InsertColumn(PipeTable t, int c, bool right)
    {
        var n = t.Copy();
        var at = right ? c + 1 : c;
        var head = FreshHeader(n.Rows[0]);
        for (var i = 0; i < n.Rows.Count; i++) n.Rows[i].Insert(at, i == 0 ? head : "");
        n.Align.Insert(at, "");
        return n;
    }

    public static PipeTable DeleteRow(PipeTable t, int r)
    {
        if (r == 0) return t;
        var n = t.Copy();
        n.Rows.RemoveAt(r);
        return n;
    }

    public static PipeTable DeleteColumn(PipeTable t, int c)
    {
        if (t.Rows[0].Count <= 1) return t;
        var n = t.Copy();
        foreach (var r in n.Rows) r.RemoveAt(c);
        n.Align.RemoveAt(c);
        return n;
    }

    public static PipeTable SetAlign(PipeTable t, int c, string align)
    {
        var n = t.Copy();
        n.Align[c] = align == "none" ? "" : align;
        return n;
    }

    // ── operations on a selection (rows and columns of the model, r0 <= r1, c0 <= c1): the grid's commands ──

    static string EscapeCell(string v) => Regex.Replace(Regex.Replace(v, @"\r?\n|\r", " ").Trim(), @"(?<!\\)\|", "\\|");
    public static string Unescape(string v) => v.Replace("\\|", "|");

    public static PipeTable SetCell(PipeTable t, int r, int c, string value)
    {
        var n = t.Copy();
        n.Rows[r][c] = EscapeCell(value);
        return n;
    }

    static Sel Norm(Sel s) => new(Math.Min(s.R0, s.R1), Math.Min(s.C0, s.C1), Math.Max(s.R0, s.R1), Math.Max(s.C0, s.C1));

    public static string ToTsv(PipeTable t, Sel s)
    {
        var (r0, c0, r1, c1) = Norm(s);
        return string.Join("\n", t.Rows.Skip(r0).Take(r1 - r0 + 1).Select(r => string.Join("\t", r.Skip(c0).Take(c1 - c0 + 1).Select(Unescape))));
    }

    // A tab-separated block (Excel, a copied range) from (r, c): cells are overwritten, rows and columns are added as it needs them
    // (new columns get a header placeholder).
    public static OpResult PasteRange(PipeTable t, int r, int c, string text)
    {
        var norm = Regex.Replace(text, @"\r\n?", "\n");
        if (norm.EndsWith('\n')) norm = norm[..^1];
        var lines = norm.Split('\n').Select(l => l.Split('\t')).ToList();
        var w = lines.Max(l => l.Length);
        var n = t.Copy();
        while (n.Rows[0].Count < c + w) n = InsertColumn(n, n.Rows[0].Count - 1, true);
        while (n.Rows.Count < r + lines.Count) n.Rows.Add(Enumerable.Repeat("", n.Rows[0].Count).ToList());
        for (var i = 0; i < lines.Count; i++)
            for (var j = 0; j < lines[i].Length; j++) n.Rows[r + i][c + j] = EscapeCell(lines[i][j]);
        return new OpResult(n, new Sel(r, c, r + lines.Count - 1, c + w - 1));
    }

    static double? Numeric(string v)
    {
        var x = Regex.Replace(Unescape(v), @"[\s,%$€£]", "");
        return x != "" && Regex.IsMatch(x, @"^[-+]?(\d+\.?\d*|\.\d+)$") ? double.Parse(x, CultureInfo.InvariantCulture) : null;
    }

    // Numbers as numbers, else text: case-insensitive, runs of digits compared by value ("a2" before "a10").
    static int Compare(string a, string b)
    {
        var x = Numeric(a); var y = Numeric(b);
        if (x is not null && y is not null) return x.Value.CompareTo(y.Value);
        return Natural(a, b);
    }

    static int Natural(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;
                var da = a[si..i].TrimStart('0'); var db = b[sj..j].TrimStart('0');
                var byLength = da.Length.CompareTo(db.Length);
                if (byLength != 0) return byLength;
                var byDigits = string.CompareOrdinal(da, db);
                if (byDigits != 0) return byDigits;
            }
            else
            {
                var cmp = string.Compare(a[i].ToString(), b[j].ToString(), CultureInfo.InvariantCulture, CompareOptions.IgnoreCase);
                if (cmp != 0) return cmp;
                i++; j++;
            }
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }

    static Sel Rect(int r0, int c0, int r1, int c1) => new(r0, c0, r1, c1);

    // The result of a command on the selection, or null when it does not apply (the header does not move, the last column stays, ...).
    public static OpResult? ApplyOp(PipeTable t, Sel s, string op, string? arg = null)
    {
        var (r0, c0, r1, c1) = Norm(s);
        int nr = r1 - r0 + 1, nc = c1 - c0 + 1, last = t.Rows.Count - 1, cols = t.Rows[0].Count;
        var n = t.Copy();
        List<string> Empty() => Enumerable.Repeat("", cols).ToList();
        switch (op)
        {
            case "insertRowAbove":
                if (r0 == 0) return null;
                n.Rows.InsertRange(r0, Enumerable.Range(0, nr).Select(_ => Empty()));
                return new OpResult(n, Rect(r0, c0, r0 + nr - 1, c1));
            case "insertRowBelow":
                n.Rows.InsertRange(r1 + 1, Enumerable.Range(0, nr).Select(_ => Empty()));
                return new OpResult(n, Rect(r1 + 1, c0, r1 + nr, c1));
            case "insertColumnLeft":
            case "insertColumnRight":
            {
                var at = op == "insertColumnLeft" ? c0 : c1 + 1;
                for (var k = 0; k < nc; k++)
                {
                    var head = FreshHeader(n.Rows[0]);
                    for (var i = 0; i < n.Rows.Count; i++) n.Rows[i].Insert(at + k, i == 0 ? head : "");
                }
                n.Align.InsertRange(at, Enumerable.Repeat("", nc));
                return new OpResult(n, Rect(r0, at, r1, at + nc - 1));
            }
            case "deleteRow":
            {
                var a = Math.Max(r0, 1);
                if (a > r1) return null;
                n.Rows.RemoveRange(a, r1 - a + 1);
                var at = Math.Min(a, n.Rows.Count - 1);
                return new OpResult(n, Rect(at, c0, at, c1));
            }
            case "deleteColumn":
            {
                if (nc >= cols) return null;
                foreach (var r in n.Rows) r.RemoveRange(c0, nc);
                n.Align.RemoveRange(c0, nc);
                var at = Math.Min(c0, cols - nc - 1);
                return new OpResult(n, Rect(r0, at, r1, at));
            }
            case "moveRowUp":
            case "moveRowDown":
            {
                var up = op == "moveRowUp";
                if (r0 < 1 || (up ? r0 < 2 : r1 >= last)) return null;
                var block = n.Rows.GetRange(r0, nr);
                n.Rows.RemoveRange(r0, nr);
                var at = up ? r0 - 1 : r0 + 1;
                n.Rows.InsertRange(at, block);
                return new OpResult(n, Rect(at, c0, at + nr - 1, c1));
            }
            case "moveColumnLeft":
            case "moveColumnRight":
            {
                var left = op == "moveColumnLeft";
                if (left ? c0 < 1 : c1 >= cols - 1) return null;
                var at = left ? c0 - 1 : c0 + 1;
                foreach (var r in n.Rows) { var block = r.GetRange(c0, nc); r.RemoveRange(c0, nc); r.InsertRange(at, block); }
                var ab = n.Align.GetRange(c0, nc); n.Align.RemoveRange(c0, nc); n.Align.InsertRange(at, ab);
                return new OpResult(n, Rect(r0, at, r1, at + nc - 1));
            }
            case "align":
                for (var c = c0; c <= c1; c++) n.Align[c] = arg == "none" ? "" : arg ?? "";
                return new OpResult(n, Rect(r0, c0, r1, c1));
            case "sortAsc":
            case "sortDesc":
            {
                var sign = op == "sortAsc" ? 1 : -1;
                int Order(List<string> a, List<string> b)
                {
                    var ea = a[c0] == ""; var eb = b[c0] == "";
                    if (ea != eb) return ea ? 1 : -1; // empty cells last, either direction
                    return ea ? 0 : sign * Compare(a[c0], b[c0]);
                }
                var body = n.Rows.Skip(1).OrderBy(r => r, Comparer<List<string>>.Create(Order)).ToList(); // OrderBy is stable
                n.Rows = [n.Rows[0], .. body];
                return new OpResult(n, Rect(r0, c0, r1, c1));
            }
            case "clear":
                for (var r = r0; r <= r1; r++)
                    for (var c = c0; c <= c1; c++) n.Rows[r][c] = "";
                return new OpResult(n, Rect(r0, c0, r1, c1));
            default: return null;
        }
    }

    // The buttons of the editor's toolbar (the move operations are not buttons: Alt+arrow maps to them, see AltMove).
    public static readonly RibbonButton[] Ribbon =
    [
        new("insertRowAbove", "Row above", "Insert row(s) above the selection"),
        new("insertRowBelow", "Row below", "Insert row(s) below the selection"),
        new("insertColumnLeft", "Column left", "Insert column(s) left of the selection"),
        new("insertColumnRight", "Column right", "Insert column(s) right of the selection"),
        new("deleteRow", "Delete row", "Delete the selected row(s)"),
        new("deleteColumn", "Delete column", "Delete the selected column(s)"),
        new("align:left", "Left", "Align column(s) left"),
        new("align:center", "Centre", "Centre column(s)"),
        new("align:right", "Right", "Align column(s) right"),
        new("sortAsc", "Sort A–Z", "Sort the rows by this column, ascending (the header stays)"),
        new("sortDesc", "Sort Z–A", "Sort the rows by this column, descending (the header stays)"),
    ];

    public static string? AltMove(string key) => key switch
    {
        "ArrowUp" => "moveRowUp", "ArrowDown" => "moveRowDown", "ArrowLeft" => "moveColumnLeft", "ArrowRight" => "moveColumnRight", _ => null,
    };

    // ── tables in a document ──

    sealed record Line(int Start, string Text, bool Table);

    static List<Line> Scan(string doc)
    {
        var at = 0;
        string? fence = null;
        var result = new List<Line>();
        foreach (var text in doc.Split('\n'))
        {
            var start = at;
            at += text.Length + 1;
            var m = Fence.Match(text);
            if (fence is not null)
            {
                if (m.Success && m.Groups[1].Value[0] == fence[0] && m.Groups[1].Value.Length >= fence.Length) fence = null;
                result.Add(new Line(start, text, false));
                continue;
            }
            if (m.Success) { fence = m.Groups[1].Value; result.Add(new Line(start, text, false)); continue; }
            result.Add(new Line(start, text, Regex.IsMatch(text, @"^ {0,3}\|")));
        }
        return result;
    }

    // The table around the caret, or null: its lines, its text and range in the document, the model row (0 for the header and
    // the separator) and column of the caret.
    public static Found? Find(string doc, int caret)
    {
        var lines = Scan(doc);
        var li = lines.FindIndex(l => caret >= l.Start && caret <= l.Start + l.Text.Length);
        if (li < 0 || !lines[li].Table) return null;
        int a = li, b = li;
        while (a > 0 && lines[a - 1].Table) a--;
        while (b < lines.Count - 1 && lines[b + 1].Table) b++;
        if (a + 1 > b || !IsSeparator(lines[a + 1].Text)) return null;
        var into = li - a;
        var off = caret - lines[li].Start;
        var cells = SplitRow(lines[li].Text);
        var col = Math.Max(0, Math.Min(cells.Count - 1, cells.Count(c => c.From <= off) - 1));
        var end = lines[b].Start + lines[b].Text.Length;
        return new Found(a, b, lines[a].Start, end, doc[lines[a].Start..end], into <= 1 ? 0 : into - 1, col, li);
    }

    // Swap the table found by Find for `t`, formatted; the selection is the content of the cell at (row, col) of the new table.
    public static TextEdit Rewrite(string doc, Found f, PipeTable t, int row, int col)
    {
        var text = Format(t);
        var lines = text.Split('\n');
        var li = row == 0 ? 0 : row + 1;
        var cell = SplitRow(lines[li])[Math.Min(col, t.Rows[0].Count - 1)];
        var before = lines.Take(li).Sum(l => l.Length + 1);
        return new TextEdit(doc[..f.Start] + text + doc[f.End..], f.Start + before + cell.Start, f.Start + before + cell.End, f.Start, f.End, text);
    }

    // Where the text of the cell (row, col) of the table `f` is in the document, whatever the table's spacing.
    public static (int Start, int End) CellOffset(string doc, Found f, int row, int col)
    {
        var lines = f.Text.Split('\n');
        var li = Math.Min(row == 0 ? 0 : row + 1, lines.Length - 1);
        var before = lines.Take(li).Sum(l => l.Length + 1);
        var cells = SplitRow(lines[li]);
        var at = f.Start + before;
        if (cells.Count == 0) return (at + lines[li].Length, at + lines[li].Length);
        var cell = cells[Math.Min(col, cells.Count - 1)];
        return (at + cell.Start, at + cell.End);
    }

    static int Columns(PipeTable t) => t.Rows[0].Count;

    public static TextEdit? Apply(string doc, int caret, string op, string? arg = null)
    {
        var f = Find(doc, caret);
        if (f is null) return null;
        var t = Parse(f.Text);
        int row = f.Row, col = f.Col;
        switch (op)
        {
            case "insertRowBelow": return Rewrite(doc, f, InsertRow(t, row, true), row + 1, col);
            case "insertRowAbove": return Rewrite(doc, f, InsertRow(t, row, false), row == 0 ? 0 : row, col);
            case "insertColumnLeft": return Rewrite(doc, f, InsertColumn(t, col, false), row, col);
            case "insertColumnRight": return Rewrite(doc, f, InsertColumn(t, col, true), row, col + 1);
            case "deleteRow": { var n = DeleteRow(t, row); return Rewrite(doc, f, n, Math.Min(row, n.Rows.Count - 1), col); }
            case "deleteColumn": { var n = DeleteColumn(t, col); return Rewrite(doc, f, n, row, Math.Min(col, Columns(n) - 1)); }
            case "align": return Rewrite(doc, f, SetAlign(t, col, arg ?? "none"), row, col);
            case "format": return Rewrite(doc, f, t, row, col);
            default:
            {
                var r = ApplyOp(t, new Sel(row, col, row, col), op, arg);
                return r is null ? null : Rewrite(doc, f, r.Table, r.Sel.R0, r.Sel.C0);
            }
        }
    }

    // Tab: the next cell (the separator row is skipped); from the last cell a row is added.
    public static TextEdit? NextCell(string doc, int caret)
    {
        var f = Find(doc, caret);
        if (f is null) return null;
        var t = Parse(f.Text);
        if (f.Col < Columns(t) - 1) return Rewrite(doc, f, t, f.Row, f.Col + 1);
        if (f.Row == t.Rows.Count - 1) return Rewrite(doc, f, InsertRow(t, f.Row, true), f.Row + 1, 0);
        return Rewrite(doc, f, t, f.Row + 1, 0);
    }

    // Shift+Tab: the previous cell, staying in the first.
    public static TextEdit? PrevCell(string doc, int caret)
    {
        var f = Find(doc, caret);
        if (f is null) return null;
        var t = Parse(f.Text);
        if (f.Col > 0) return Rewrite(doc, f, t, f.Row, f.Col - 1);
        if (f.Row > 0) return Rewrite(doc, f, t, f.Row - 1, Columns(t) - 1);
        return Rewrite(doc, f, t, 0, 0);
    }

    // Enter at the end of a row (nothing but its closing pipe after the caret) adds a row below; anywhere else null.
    public static TextEdit? Enter(string doc, int caret)
    {
        var f = Find(doc, caret);
        if (f is null) return null;
        var nl = doc.IndexOf('\n', caret);
        var rest = doc[caret..(nl < 0 ? doc.Length : nl)];
        if (!Regex.IsMatch(rest, @"^\s*\|?\s*$")) return null;
        return Rewrite(doc, f, InsertRow(Parse(f.Text), f.Row, true), f.Row + 1, 0);
    }

    // ── moving in the grid, and leaving it ──

    // Where a navigation key leads from (r, c) in a grid of rows x cols cells; Leave "after" / "before" when an arrow goes past the
    // last / first row; null for any other key. Sideways the cell stays at the edge. Home / End: first / last cell of the row; with ctrl, of the table.
    public static Nav? Navigate(string key, int r, int c, int rows, int cols, bool ctrl = false)
    {
        int lr = rows - 1, lc = cols - 1;
        return key switch
        {
            "ArrowUp" => r > 0 ? new Nav(r - 1, c) : new Nav(0, 0, "before"),
            "ArrowDown" => r < lr ? new Nav(r + 1, c) : new Nav(0, 0, "after"),
            "ArrowLeft" => new Nav(r, Math.Max(0, c - 1)),
            "ArrowRight" => new Nav(r, Math.Min(lc, c + 1)),
            "Home" => ctrl ? new Nav(0, 0) : new Nav(r, 0),
            "End" => ctrl ? new Nav(lr, lc) : new Nav(r, lc),
            _ => null,
        };
    }

    // The text change that takes the caret out of the table `f`: to the start of the line after it ("after"), or the end of the line
    // before it ("before"); an empty line is made when the table is at the end / the start of the text.
    public static TextEdit Leave(string doc, Found f, string where)
    {
        int rs, at;
        var rep = "";
        if (where == "before")
        {
            if (f.Start > 0) rs = at = f.Start - 1; else { rs = at = 0; rep = "\n"; }
        }
        else if (doc.Length > f.End) rs = at = f.End + 1;
        else { rs = f.End; rep = "\n"; at = f.End + 1; }
        return new TextEdit(doc[..rs] + rep + doc[rs..], at, at, rs, rs, rep);
    }

    // ── /table ──

    public static string Create(int bodyRows, int cols) => Format(new PipeTable
    {
        Rows = [Enumerable.Range(0, cols).Select(i => "Column " + (i + 1)).ToList(), .. Enumerable.Range(0, bodyRows).Select(_ => Enumerable.Repeat("", cols).ToList())],
        Align = Enumerable.Repeat("", cols).ToList(),
    });

    // `/table` typed alone on its line (indentation allowed, nothing after the caret): its range, else null.
    public static (int Start, int End)? SlashAt(string doc, int caret)
    {
        var ls = caret > 0 ? LastNewline(doc, caret - 1) + 1 : 0;
        var m = Regex.Match(doc[ls..caret], @"^([ \t]*)/table$");
        var nl = doc.IndexOf('\n', caret);
        var after = doc[caret..(nl < 0 ? doc.Length : nl)];
        return m.Success && after.Trim() == "" ? (ls + m.Groups[1].Length, caret) : null;
    }

    // A new table in place of the `/table` at `range`; a blank line goes before it when the line above has text. The selection is the first header cell.
    public static TextEdit InsertAt(string doc, (int Start, int End) range, int bodyRows, int cols)
    {
        var table = Create(bodyRows, cols);
        var lineAbove = range.Start > 0 ? doc[(LastNewline(doc, range.Start - 2) + 1)..(range.Start - 1)] : "";
        var lead = range.Start > 0 && doc[range.Start - 1] == '\n' && lineAbove.Trim() != "" ? "\n" : "";
        var first = SplitRow(table.Split('\n')[0])[0];
        return new TextEdit(doc[..range.Start] + lead + table + doc[range.End..], range.Start + lead.Length + first.Start,
            range.Start + lead.Length + first.End, range.Start, range.End, lead + table);
    }
}
