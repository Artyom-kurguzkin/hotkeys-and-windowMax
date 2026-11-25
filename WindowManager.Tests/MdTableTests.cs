namespace WindowManager.Tests;

// Pure pipe-table operations (MdTable.cs), ported case by case from repo-task-manager's tests/md-table.test.mjs.
// Model rows: 0 is the header, 1.. the body (the separator row is not a row of the model). Documents are plain strings, carets are offsets.
public class MdTableTests
{
    static string L(params string[] lines) => string.Join("\n", lines);
    static string[] Cells(string line) => line.Split('|')[1..^1].Select(c => c.Trim()).ToArray();
    static string[][] Grid(string text) => text.Split('\n').Select(Cells).ToArray();
    static string Pipes(string line) => string.Join(",", line.Select((c, i) => c == '|' ? i : -1).Where(i => i >= 0));
    static PipeTable Base() => MdTable.Parse(L("| a | b |", "| --- | --- |", "| 1 | 2 |"));
    static PipeTable Sample() => MdTable.Parse(L("| name | qty |", "| --- | --- |", "| pear | 10 |", "| apple | 9 |", "| fig | 100 |"));
    static string[] Body(PipeTable t, int c = 0) => t.Rows.Skip(1).Select(r => r[c]).ToArray();
    static Sel S(int r0, int c0, int r1, int c1) => new(r0, c0, r1, c1);

    [Fact]
    public void Format_aligns_a_ragged_table_every_column_padded_to_its_widest_cell_the_separator_as_wide()
    {
        var output = MdTable.Format(MdTable.Parse(L("| a | bbb |", "|-|-|", "| cccc |")));
        Assert.Equal(L("| a    | bbb |", "| ---- | --- |", "| cccc |     |"), output);
    }

    [Fact]
    public void Format_keeps_the_pipes_of_every_line_in_the_same_columns()
    {
        var lines = MdTable.Format(MdTable.Parse(L("|x|yy|", "|---|---|", "|1|"))).Split('\n');
        Assert.Single(lines.Select(Pipes).Distinct());
    }

    [Fact]
    public void Insert_row_below_puts_an_empty_row_after_the_caret_row_below_the_header_it_becomes_the_first_body_row()
    {
        var below = Grid(MdTable.Format(MdTable.InsertRow(Base(), 1, true)));
        Assert.Equal(4, below.Length);
        Assert.Equal(new[] { "", "" }, below[3]);
        var fromHeader = Grid(MdTable.Format(MdTable.InsertRow(Base(), 0, true)));
        Assert.Equal(new[] { "", "" }, fromHeader[2]);
        Assert.Equal(new[] { "1", "2" }, fromHeader[3]);
    }

    [Fact]
    public void Insert_row_above_puts_an_empty_row_before_the_caret_row_above_the_header_is_refused()
    {
        var above = Grid(MdTable.Format(MdTable.InsertRow(Base(), 1, false)));
        Assert.Equal(new[] { "", "" }, above[2]);
        Assert.Equal(new[] { "1", "2" }, above[3]);
        Assert.Equal(2, MdTable.InsertRow(Base(), 0, false).Rows.Count);
    }

    [Fact]
    public void Insert_column_right_of_the_caret_column_adds_an_empty_column_with_a_header_placeholder_left_goes_before_it()
    {
        var right = Grid(MdTable.Format(MdTable.InsertColumn(Base(), 0, true)));
        Assert.Equal(new[] { "a", "Column 3", "b" }, right[0]); // no "Column N" header yet: the column count + 1
        Assert.Equal(new[] { "1", "", "2" }, right[2]);
        var left = MdTable.InsertColumn(Base(), 0, false);
        Assert.Equal("a", left.Rows[0][1]);
        Assert.Equal(3, left.Rows[0].Count);
    }

    [Fact]
    public void A_new_columns_header_is_Column_N_one_more_than_the_highest_Column_number_never_a_copy_of_its_neighbour()
    {
        var t = MdTable.Parse(L("| Column 1 | Column 2 | Column 3 | Column 4 |", "| --- | --- | --- | --- |", "| | | | |"));
        Assert.Equal("Column 5", MdTable.InsertColumn(t, 3, true).Rows[0][4]);
        Assert.Equal("Column 5", MdTable.InsertColumn(t, 0, false).Rows[0][0]); // left of the first: still the next number
        var twice = MdTable.InsertColumn(MdTable.InsertColumn(t, 3, true), 4, true);
        Assert.Equal(new[] { "Column 4", "Column 5", "Column 6" }, twice.Rows[0].Skip(3));
        var gap = MdTable.Parse(L("| Column 7 | x |", "| --- | --- |", "| | |"));
        Assert.Equal("Column 8", MdTable.InsertColumn(gap, 1, true).Rows[0][2]);
        var named = MdTable.Parse(L("| name | qty | Column 2 |", "| --- | --- | --- |", "| | | |"));
        Assert.Equal("Column 3", MdTable.InsertColumn(named, 0, true).Rows[0][1]);
        Assert.Equal("Column 3", MdTable.InsertColumn(Base(), 1, true).Rows[0][2]); // a, b: the count + 1
    }

    [Fact]
    public void ApplyOp_and_Apply_give_every_new_column_a_fresh_header_several_selected_columns_get_consecutive_numbers()
    {
        var t = MdTable.Parse(L("| Column 1 | Column 2 | Column 3 | Column 4 |", "| --- | --- | --- | --- |", "| | | | |"));
        var one = MdTable.ApplyOp(t, S(1, 3, 1, 3), "insertColumnRight")!;
        Assert.Equal(new[] { "Column 1", "Column 2", "Column 3", "Column 4", "Column 5" }, one.Table.Rows[0]);
        var two = MdTable.ApplyOp(t, S(1, 1, 1, 2), "insertColumnLeft")!;
        Assert.Equal(new[] { "Column 1", "Column 5", "Column 6", "Column 2", "Column 3", "Column 4" }, two.Table.Rows[0]);
        var doc = MdTable.Format(t);
        var r = MdTable.Apply(doc, doc.IndexOf("Column 4"), "insertColumnRight")!;
        Assert.Equal("| Column 1 | Column 2 | Column 3 | Column 4 | Column 5 |", r.Text.Split('\n')[0]);
    }

    [Fact]
    public void The_ribbon_has_no_move_buttons_its_operations_are_insert_delete_align_sort()
    {
        var ops = MdTable.Ribbon.Select(b => b.Op).ToArray();
        Assert.Equal(new[] { "insertRowAbove", "insertRowBelow", "insertColumnLeft", "insertColumnRight", "deleteRow", "deleteColumn",
            "align:left", "align:center", "align:right", "sortAsc", "sortDesc" }, ops);
        Assert.DoesNotContain(ops, o => o.StartsWith("move"));
    }

    [Fact]
    public void The_moves_stay_as_operations_for_the_grids_Alt_arrow_keys()
    {
        Assert.Equal("moveRowUp", MdTable.AltMove("ArrowUp"));
        Assert.Equal("moveRowDown", MdTable.AltMove("ArrowDown"));
        Assert.Equal("moveColumnLeft", MdTable.AltMove("ArrowLeft"));
        Assert.Equal("moveColumnRight", MdTable.AltMove("ArrowRight"));
        Assert.Null(MdTable.AltMove("a"));
    }

    [Fact]
    public void Delete_row_removes_the_caret_row_the_header_cannot_be_deleted()
    {
        var t = MdTable.Parse(L("| a | b |", "| --- | --- |", "| 1 | 2 |", "| 3 | 4 |"));
        Assert.Equal(new[] { "a", "---", "3" }, Grid(MdTable.Format(MdTable.DeleteRow(t, 1))).Select(r => r[0]));
        Assert.Equal(3, MdTable.DeleteRow(t, 0).Rows.Count);
    }

    [Fact]
    public void Delete_column_removes_the_caret_column_the_last_column_stays()
    {
        var output = Grid(MdTable.Format(MdTable.DeleteColumn(Base(), 0)));
        Assert.Equal(new[] { "b" }, output[0]);
        Assert.Equal(new[] { "2" }, output[2]);
        var one = MdTable.Parse(L("| a |", "| --- |", "| 1 |"));
        Assert.Equal(1, MdTable.DeleteColumn(one, 0).Rows[0].Count);
    }

    [Fact]
    public void Alignment_markers_left_centre_and_right_put_a_colon_on_the_separator_of_the_caret_column_none_clears_it()
    {
        string[] Sep(int c, string al) => Cells(MdTable.Format(MdTable.SetAlign(Base(), c, al)).Split('\n')[1]);
        Assert.Equal(new[] { ":---", "---" }, Sep(0, "left"));
        Assert.Equal(new[] { ":---:", "---" }, Sep(0, "center"));
        Assert.Equal(new[] { "---", "---:" }, Sep(1, "right"));
        Assert.Equal(new[] { "---", "---" }, Sep(0, "none"));
        // a marker survives a parse and a format
        var again = MdTable.Format(MdTable.Parse(MdTable.Format(MdTable.SetAlign(Base(), 0, "center")))).Split('\n')[1];
        Assert.Equal(":---:", Cells(again)[0]);
    }

    [Fact]
    public void Right_aligned_text_pads_on_the_left_centred_text_on_both_sides()
    {
        var t = MdTable.SetAlign(MdTable.SetAlign(MdTable.Parse(L("| aaaa | bbbb |", "| --- | --- |", "| 1 | 2 |")), 0, "right"), 1, "center");
        Assert.Equal("|    1 |   2   |", MdTable.Format(t).Split('\n')[2]); // a centred column is at least 5 wide: ":---:"
    }

    [Fact]
    public void InsertAt_swaps_slash_table_for_a_table_with_its_first_header_cell_selected_and_leaves_a_blank_line_after_text_above()
    {
        var doc = "intro\n/table";
        var r = MdTable.InsertAt(doc, MdTable.SlashAt(doc, doc.Length)!.Value, 2, 2);
        Assert.Equal(L("intro", "", "| Column 1 | Column 2 |", "| -------- | -------- |", "|          |          |", "|          |          |"), r.Text);
        Assert.Equal("Column 1", r.Text[r.Start..r.End]);
        var alone = MdTable.InsertAt("/table", (0, 6), 1, 1);
        Assert.Equal(3, alone.Text.Split('\n').Length);
        Assert.Equal("Column 1", alone.Text[alone.Start..alone.End]);
    }

    [Fact]
    public void Tab_from_the_last_cell_adds_a_row_and_lands_in_its_first_cell_Tab_inside_moves_on_and_skips_the_separator_row()
    {
        var doc = MdTable.Format(Base());
        var last = MdTable.NextCell(doc, doc.LastIndexOf('2'))!;
        Assert.Equal(4, last.Text.Split('\n').Length);
        Assert.Equal(string.Join("\n", last.Text.Split('\n').Take(3)).Length + 3, last.Start); // "\n" + "| "
        Assert.Equal(last.Start, last.End); // an empty cell: a caret
        var a = MdTable.NextCell(doc, doc.IndexOf('a'))!;
        Assert.Equal(doc, a.Text);
        Assert.Equal("b", doc[a.Start..a.End]);
        var h = MdTable.NextCell(doc, doc.IndexOf('b'))!;
        Assert.Equal("1", doc[h.Start..h.End]);
    }

    [Fact]
    public void Shift_Tab_moves_to_the_previous_cell_and_stays_in_the_first_one()
    {
        var doc = MdTable.Format(Base());
        var p = MdTable.PrevCell(doc, doc.IndexOf('b'))!;
        Assert.Equal("a", doc[p.Start..p.End]);
        var first = MdTable.PrevCell(doc, doc.IndexOf('a'))!;
        Assert.Equal("a", doc[first.Start..first.End]);
        var fromBody = MdTable.PrevCell(doc, doc.IndexOf('1'))!;
        Assert.Equal("b", doc[fromBody.Start..fromBody.End]); // back over the separator
    }

    [Fact]
    public void Tab_outside_a_table_does_nothing()
    {
        Assert.Null(MdTable.NextCell("plain text", 3));
    }

    [Fact]
    public void Enter_at_the_end_of_a_row_adds_a_row_below_and_lands_in_its_first_cell_mid_cell_it_is_left_alone()
    {
        var doc = MdTable.Format(Base());
        var e = MdTable.Enter(doc, doc.Length)!;
        Assert.Equal(4, e.Text.Split('\n').Length);
        Assert.Null(MdTable.Enter(doc, doc.IndexOf('1')));
    }

    [Fact]
    public void Find_ignores_lines_inside_code_fences_and_reports_the_table_around_the_caret_with_its_row_and_column()
    {
        var doc = L("text", "```", "| a | b |", "| - | - |", "```", "", "| x | y |", "| --- | --- |", "| 1 | 2 |", "after");
        Assert.Null(MdTable.Find(doc, doc.IndexOf("| a")));
        var f = MdTable.Find(doc, doc.IndexOf('2'))!;
        Assert.Equal(6, f.StartLine);
        Assert.Equal(8, f.EndLine);
        Assert.Equal(1, f.Row);
        Assert.Equal(1, f.Col);
        Assert.Equal(L("| x | y |", "| --- | --- |", "| 1 | 2 |"), doc[f.Start..f.End]);
        Assert.Null(MdTable.Find(doc, doc.IndexOf("after")));
    }

    [Fact]
    public void A_run_of_pipe_lines_without_a_separator_row_is_not_a_table()
    {
        Assert.Null(MdTable.Find(L("| a | b |", "| 1 | 2 |"), 2));
    }

    [Fact]
    public void Escaped_pipes_and_pipes_in_code_spans_stay_inside_their_cell()
    {
        var t = MdTable.Parse(L("| a \\| b | `c|d` |", "| --- | --- |", "| 1 | 2 |"));
        Assert.Equal(2, t.Rows[0].Count);
        Assert.Equal("a \\| b", t.Rows[0][0]);
    }

    [Fact]
    public void Apply_runs_an_operation_on_the_table_at_the_caret_and_returns_the_whole_text_with_the_caret_in_the_cell_it_worked_on()
    {
        var doc = L("intro", "", "| a | b |", "| --- | --- |", "| 1 | 2 |", "", "outro");
        var r = MdTable.Apply(doc, doc.IndexOf('2'), "insertRowBelow")!;
        Assert.Equal(L("intro", "", "| a   | b   |", "| --- | --- |", "| 1   | 2   |", "|     |     |", "", "outro"), r.Text);
        Assert.Equal(r.Start, r.End);
        Assert.Equal(6, r.Text[..r.Start].Split('\n').Length); // the caret is on the new line
        Assert.Null(MdTable.Apply("no table", 2, "deleteRow"));
    }

    [Fact]
    public void Create_makes_a_header_a_separator_and_the_chosen_empty_body_rows_aligned()
    {
        var output = MdTable.Create(3, 4).Split('\n');
        Assert.Equal(2 + 3, output.Length);
        Assert.Equal("| Column 1 | Column 2 | Column 3 | Column 4 |", output[0]);
        Assert.Equal("| -------- | -------- | -------- | -------- |", output[1]);
        Assert.Single(output.Select(Pipes).Distinct());
    }

    [Fact]
    public void SlashAt_finds_slash_table_alone_on_its_line_or_after_indentation_only()
    {
        Assert.Equal((6, 12), MdTable.SlashAt("hello\n/table", 12));
        Assert.Equal((2, 8), MdTable.SlashAt("  /table", 8));
        Assert.Null(MdTable.SlashAt("a /table", 8));
        Assert.Null(MdTable.SlashAt("/tabl", 5));
    }

    // ── the grid: operations on a selection (rows r0..r1, columns c0..c1 of the model) ──

    [Fact]
    public void Move_rows_down_and_up_swaps_a_block_of_body_rows_with_its_neighbour_and_the_selection_follows()
    {
        var down = MdTable.ApplyOp(Sample(), S(1, 0, 1, 1), "moveRowDown")!;
        Assert.Equal(new[] { "apple", "pear", "fig" }, Body(down.Table));
        Assert.Equal((2, 2), (down.Sel.R0, down.Sel.R1));
        var up = MdTable.ApplyOp(Sample(), S(2, 0, 3, 1), "moveRowUp")!;
        Assert.Equal(new[] { "apple", "fig", "pear" }, Body(up.Table));
        Assert.Equal((1, 2), (up.Sel.R0, up.Sel.R1));
    }

    [Fact]
    public void Move_rows_never_moves_the_header_never_moves_a_row_above_the_first_body_row_or_below_the_last()
    {
        Assert.Null(MdTable.ApplyOp(Sample(), S(0, 0, 0, 1), "moveRowDown"));
        Assert.Null(MdTable.ApplyOp(Sample(), S(1, 0, 1, 1), "moveRowUp"));
        Assert.Null(MdTable.ApplyOp(Sample(), S(3, 0, 3, 1), "moveRowDown"));
    }

    [Fact]
    public void Move_column_right_and_left_carries_the_header_the_cells_and_the_alignment_with_it()
    {
        var t = MdTable.SetAlign(Sample(), 1, "right");
        var r = MdTable.ApplyOp(t, S(0, 1, 4, 1), "moveColumnLeft")!;
        Assert.Equal(new[] { "qty", "name" }, r.Table.Rows[0]);
        Assert.Equal(new[] { "right", "" }, r.Table.Align);
        Assert.Equal((0, 0), (r.Sel.C0, r.Sel.C1));
        Assert.Null(MdTable.ApplyOp(t, S(0, 1, 4, 1), "moveColumnRight"));
        var back = MdTable.ApplyOp(r.Table, S(0, 0, 4, 0), "moveColumnRight")!;
        Assert.Equal(new[] { "name", "qty" }, back.Table.Rows[0]);
    }

    [Fact]
    public void Sort_ascending_and_descending_reorders_the_body_only_the_header_and_the_separator_row_stay_on_top()
    {
        var asc = MdTable.ApplyOp(Sample(), S(1, 0, 1, 0), "sortAsc")!.Table;
        Assert.Equal(new[] { "name", "qty" }, asc.Rows[0]);
        Assert.Equal(new[] { "apple", "fig", "pear" }, Body(asc));
        Assert.Equal(new[] { "9", "100", "10" }, Body(asc, 1)); // rows move whole
        var output = MdTable.Format(asc).Split('\n');
        Assert.Matches(@"^\| -+ \| -+ \|$", output[1]);
        var desc = MdTable.ApplyOp(Sample(), S(0, 0, 0, 0), "sortDesc")!.Table;
        Assert.Equal(new[] { "pear", "fig", "apple" }, Body(desc));
    }

    [Fact]
    public void Sort_compares_numbers_as_numbers_keeps_empty_cells_last_and_equal_cells_in_their_order()
    {
        var t = MdTable.Parse(L("| n | tag |", "| - | - |", "| 10 | a |", "|  | b |", "| 9 | c |", "| 10 | d |"));
        var asc = MdTable.ApplyOp(t, S(1, 0, 1, 0), "sortAsc")!.Table;
        Assert.Equal(new[] { "c", "a", "d", "b" }, Body(asc, 1));
        var desc = MdTable.ApplyOp(t, S(1, 0, 1, 0), "sortDesc")!.Table;
        Assert.Equal(new[] { "a", "d", "c", "b" }, Body(desc, 1));
    }

    [Fact]
    public void Clear_empties_the_cells_of_the_selected_range_and_nothing_else()
    {
        var r = MdTable.ApplyOp(Sample(), S(1, 0, 2, 0), "clear")!;
        Assert.Equal(new[] { "", "", "fig" }, Body(r.Table));
        Assert.Equal(new[] { "10", "9", "100" }, Body(r.Table, 1));
        Assert.Equal(new[] { "name", "qty" }, r.Table.Rows[0]);
    }

    [Fact]
    public void Paste_of_a_tab_separated_block_fills_cells_from_the_selection_and_grows_the_table_in_both_directions()
    {
        var r = MdTable.PasteRange(Sample(), 3, 1, "x\ty\tz\r\n1\t2\t3\r\n");
        Assert.Equal(5, r.Table.Rows.Count); // header + 3 body rows + 1 new
        Assert.Equal(4, r.Table.Rows[0].Count);
        Assert.Equal(new[] { "fig", "x", "y", "z" }, r.Table.Rows[3]);
        Assert.Equal(new[] { "", "1", "2", "3" }, r.Table.Rows[4]);
        Assert.Equal("Column 3", r.Table.Rows[0][2]); // new columns get header placeholders
        Assert.Equal((3, 1, 4, 3), (r.Sel.R0, r.Sel.C0, r.Sel.R1, r.Sel.C1));
        var taller = MdTable.PasteRange(Sample(), 3, 0, "1\n2\n3");
        Assert.Equal(6, taller.Table.Rows.Count);
        Assert.Equal(new[] { "3", "" }, taller.Table.Rows[5]);
    }

    [Fact]
    public void Paste_of_a_2x2_block_lands_where_the_selection_starts_and_a_pipe_in_pasted_text_is_escaped()
    {
        var r = MdTable.PasteRange(Sample(), 1, 0, "a|b\tc\nd\te");
        Assert.Equal(new[] { "a\\|b", "c" }, r.Table.Rows[1]);
        Assert.Equal(new[] { "d", "e" }, r.Table.Rows[2]);
        Assert.Equal((1, 0, 2, 1), (r.Sel.R0, r.Sel.C0, r.Sel.R1, r.Sel.C1));
        Assert.Equal("a\\|b", MdTable.Parse(MdTable.Format(r.Table)).Rows[1][0]);
    }

    [Fact]
    public void ToTsv_gives_the_selected_rectangle_as_tab_separated_lines_pipes_unescaped()
    {
        var t = MdTable.SetCell(Sample(), 1, 0, "p|q");
        Assert.Equal("name\tqty\np|q\t10", MdTable.ToTsv(t, S(0, 0, 1, 1)));
    }

    [Fact]
    public void SetCell_trims_flattens_line_breaks_and_escapes_pipes()
    {
        var t = MdTable.SetCell(Sample(), 2, 1, "  a\nb | c ");
        Assert.Equal("a b \\| c", t.Rows[2][1]);
        Assert.Equal("9", Sample().Rows[2][1]); // the original is untouched
    }

    [Fact]
    public void Multi_row_operations_insert_as_many_rows_as_selected_delete_a_block_of_rows_or_columns_align_several_columns()
    {
        var ins = MdTable.ApplyOp(Sample(), S(2, 0, 3, 0), "insertRowAbove")!;
        Assert.Equal(6, ins.Table.Rows.Count);
        Assert.Equal((2, 3), (ins.Sel.R0, ins.Sel.R1));
        Assert.Equal(new[] { "apple", "9" }, ins.Table.Rows[4]);
        var del = MdTable.ApplyOp(Sample(), S(1, 0, 2, 1), "deleteRow")!;
        Assert.Equal(new[] { "fig" }, Body(del.Table));
        Assert.Null(MdTable.ApplyOp(Sample(), S(0, 0, 0, 1), "deleteRow")); // the header stays
        Assert.Null(MdTable.ApplyOp(Sample(), S(0, 0, 4, 1), "deleteColumn")); // the last columns stay
        var al = MdTable.ApplyOp(Sample(), S(0, 0, 4, 1), "align", "center")!;
        Assert.Equal(new[] { "center", "center" }, al.Table.Align);
        var col = MdTable.ApplyOp(Sample(), S(1, 1, 1, 1), "insertColumnRight")!;
        Assert.Equal(new[] { "name", "qty", "Column 3" }, col.Table.Rows[0]);
        Assert.Equal((2, 2), (col.Sel.C0, col.Sel.C1));
    }

    [Fact]
    public void Apply_runs_the_new_operations_on_the_table_at_the_caret_too()
    {
        var doc = MdTable.Format(Sample());
        string[] FirstColumn(string text) => text.Split('\n').Skip(2).Select(l => l.Split('|')[1].Trim()).ToArray();
        Assert.Equal(new[] { "apple", "pear", "fig" }, FirstColumn(MdTable.Apply(doc, doc.IndexOf("pear"), "moveRowDown")!.Text));
        Assert.Equal(new[] { "pear", "fig", "apple" }, FirstColumn(MdTable.Apply(doc, doc.IndexOf("name"), "sortDesc")!.Text));
        Assert.Null(MdTable.Apply(doc, doc.IndexOf("pear"), "moveRowUp"));
    }

    [Fact]
    public void CellOffset_is_where_a_cells_text_is_in_the_document_for_a_table_that_is_not_formatted()
    {
        var doc = L("x", "|a|bb|", "|-|-|", "| 1 |  22 |");
        var f = MdTable.Find(doc, doc.IndexOf("bb"))!;
        var h = MdTable.CellOffset(doc, f, 0, 1);
        var c = MdTable.CellOffset(doc, f, 1, 1);
        Assert.Equal("bb", doc[h.Start..h.End]);
        Assert.Equal("22", doc[c.Start..c.End]);
    }

    // ── leaving the grid and moving in it ──

    [Fact]
    public void Navigate_arrows_move_one_cell_and_stay_inside_the_table_sideways()
    {
        Assert.Equal(new Nav(2, 1), MdTable.Navigate("ArrowDown", 1, 1, 4, 3));
        Assert.Equal(new Nav(1, 1), MdTable.Navigate("ArrowUp", 2, 1, 4, 3));
        Assert.Equal(new Nav(2, 0), MdTable.Navigate("ArrowLeft", 2, 0, 4, 3));
        Assert.Equal(new Nav(2, 2), MdTable.Navigate("ArrowRight", 2, 2, 4, 3));
    }

    [Fact]
    public void Navigate_ArrowDown_on_the_last_row_leaves_below_the_table_ArrowUp_on_the_first_row_leaves_above_it()
    {
        Assert.Equal(new Nav(0, 0, "after"), MdTable.Navigate("ArrowDown", 3, 1, 4, 3));
        Assert.Equal(new Nav(0, 0, "before"), MdTable.Navigate("ArrowUp", 0, 1, 4, 3));
        Assert.Equal(new Nav(0, 0, "after"), MdTable.Navigate("ArrowDown", 0, 0, 1, 1)); // a header-only table
    }

    [Fact]
    public void Navigate_Home_and_End_go_to_the_first_and_last_cell_of_the_row_with_Ctrl_of_the_table()
    {
        Assert.Equal(new Nav(2, 0), MdTable.Navigate("Home", 2, 2, 4, 3));
        Assert.Equal(new Nav(2, 2), MdTable.Navigate("End", 2, 0, 4, 3));
        Assert.Equal(new Nav(0, 0), MdTable.Navigate("Home", 2, 2, 4, 3, true));
        Assert.Equal(new Nav(3, 2), MdTable.Navigate("End", 1, 0, 4, 3, true));
        Assert.Null(MdTable.Navigate("a", 1, 0, 4, 3));
    }

    [Fact]
    public void Leave_below_puts_the_caret_on_the_line_after_the_table_at_the_end_of_the_text_an_empty_line_is_made()
    {
        var doc = L("intro", "", "| a | b |", "| - | - |", "| 1 | 2 |", "after", "more");
        var r = MdTable.Leave(doc, MdTable.Find(doc, doc.IndexOf('1'))!, "after");
        Assert.Equal("", r.Replacement);
        Assert.Equal(doc, r.Text);
        Assert.Equal(L("after", "more"), doc[r.Start..]);
        var end = L("| a |", "| - |", "| 1 |");
        var e = MdTable.Leave(end, MdTable.Find(end, end.IndexOf('1'))!, "after");
        Assert.Equal(end + "\n", e.Text);
        Assert.Equal(e.Text.Length, e.Start);
        Assert.Equal((end.Length, end.Length, "\n"), (e.RangeStart, e.RangeEnd, e.Replacement));
        var nl = end + "\n"; // a newline already ends the text: the caret goes on that empty last line, nothing is added
        var n = MdTable.Leave(nl, MdTable.Find(nl, nl.IndexOf('1'))!, "after");
        Assert.Equal(nl, n.Text);
        Assert.Equal(nl.Length, n.Start);
    }

    [Fact]
    public void Leave_above_puts_the_caret_at_the_end_of_the_line_before_the_table_at_the_start_of_the_text_an_empty_line_is_made()
    {
        var doc = L("intro", "", "| a | b |", "| - | - |", "| 1 | 2 |");
        var r = MdTable.Leave(doc, MdTable.Find(doc, doc.IndexOf('a'))!, "before");
        Assert.Equal(doc, r.Text);
        Assert.Equal(doc.IndexOf("| a") - 1, r.Start);
        Assert.Equal("intro\n", doc[..r.Start]);
        var top = L("| a |", "| - |", "| 1 |");
        var t = MdTable.Leave(top, MdTable.Find(top, 2)!, "before");
        Assert.Equal("\n" + top, t.Text);
        Assert.Equal(0, t.Start);
        Assert.Equal((0, 0, "\n"), (t.RangeStart, t.RangeEnd, t.Replacement));
        var text = L("intro", "| a |", "| - |", "| 1 |"); // text directly above: the caret ends that line
        var x = MdTable.Leave(text, MdTable.Find(text, 8)!, "before");
        Assert.Equal("intro", text[..x.Start]);
    }
}
