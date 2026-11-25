using System.Windows.Automation;

namespace WindowManager.Tests;

// The re-edit building blocks (TableReedit.cs): the pure table-range lookup and Read on elements that have no table.
// The live UIA read (a caret in a real Notepad table) is not covered here: it needs a real text control.
public class TableReeditTests
{
    static string L(params string[] lines) => string.Join("\n", lines);

    [Fact]
    public void Leaving_the_grid_by_keyboard_mutes_the_table_as_written_a_click_away_does_not()
    {
        var table = L("| a | b |", "| - | - |", "| x | y |");
        Assert.Equal(table, TableReedit.MuteAfterClose(new PopupResult(table + "\n", "left", false)));   // Esc out of the grid (T.leave's extra line)
        Assert.Equal(table, TableReedit.MuteAfterClose(new PopupResult(table, "escape", false)));
        Assert.Equal(table, TableReedit.MuteAfterClose(new PopupResult("\n" + table, "astext", true))); // Edit as text; an arrow up past the first row
        Assert.Null(TableReedit.MuteAfterClose(new PopupResult(table, "collect", false)));              // clicked away: the click may be back into it
        Assert.Null(TableReedit.MuteAfterClose(new PopupResult("/table", "escape", false)));            // picker cancelled: no table
    }

    [Fact]
    public void TableAround_finds_the_whole_table_from_any_of_its_lines()
    {
        var table = L("| a | b |", "| - | - |", "| 1 | 2 |");
        var doc = "intro\n\n" + table + "\n\noutro";
        var start = "intro\n\n".Length;
        var end = start + table.Length;
        foreach (var caret in new[] { start, start + 3, start + 10, start + 15, end })
            Assert.Equal((start, end), TableReedit.TableAround(doc, caret));
        Assert.Equal((0, table.Length), TableReedit.TableAround(table, 0)); // the table is the whole text, no trailing newline
        Assert.Equal((0, table.Length), TableReedit.TableAround(table + "\n", table.Length)); // end of the last line, before the newline
    }

    [Fact]
    public void TableAround_is_null_outside_a_table_and_inside_a_code_fence()
    {
        Assert.Null(TableReedit.TableAround("just text\n| a |\nmore", 12)); // no separator row: not a table
        var doc = L("before", "", "| a | b |", "| - | - |", "", "after");
        Assert.Null(TableReedit.TableAround(doc, 2));
        Assert.Null(TableReedit.TableAround(doc, doc.Length));
        var fenced = L("```", "| a | b |", "| - | - |", "```");
        Assert.Null(TableReedit.TableAround(fenced, 8));
        Assert.Null(TableReedit.TableAround("", 0));
    }

    [Fact]
    public void LooksLikeTableLine_accepts_up_to_three_spaces_of_indent()
    {
        Assert.True(TableReedit.LooksLikeTableLine("| a |"));
        Assert.True(TableReedit.LooksLikeTableLine("|"));
        Assert.True(TableReedit.LooksLikeTableLine("   | a |"));
        Assert.False(TableReedit.LooksLikeTableLine("    | a |"));
        Assert.False(TableReedit.LooksLikeTableLine("a | b |"));
        Assert.False(TableReedit.LooksLikeTableLine("\t| a |"));
        Assert.False(TableReedit.LooksLikeTableLine(""));
    }

    [Fact]
    public void Read_returns_null_for_a_null_or_non_text_element()
    {
        // The desktop root has no TextPattern. (AutomationElement.FocusedElement is not used: no live desktop in a test.)
        Assert.Null(TableReedit.Read(AutomationElement.RootElement));
        Assert.Null(TableReedit.Read(AutomationElement.RootElement)); // second time: same answer, no second log line
    }
}
