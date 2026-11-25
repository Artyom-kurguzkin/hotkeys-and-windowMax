using System.Drawing;
using System.Windows.Forms;

namespace WindowManager.Tests;

public class TableEditorHotkeyTests
{
    [Fact]
    public void Typing_the_command_at_a_word_start_fires_once()
    {
        var t = new SlashTrigger();
        Assert.Equal([false, false, false, false, false, true], "/table".Select(t.Type).ToArray());
        Assert.False(t.Type('e')); // fired already, the buffer was cleared
        foreach (var c in "hello /tabl") Assert.False(t.Type(c));
        Assert.True(t.Type('e')); // after whitespace
        foreach (var c in "x/tabl") Assert.False(t.Type(c));
        Assert.False(t.Type('e')); // inside a word: not the command
        t.Reset(); // Enter
        Assert.Equal([false, false, false, false, false, true, false, false], "/tableau".Select(t.Type).ToArray()); // fires at the e, like an editor's slash menu
    }

    [Fact]
    public void A_boundary_or_a_backspace_edits_what_was_typed()
    {
        var t = new SlashTrigger();
        foreach (var c in "abc") t.Type(c);
        t.Reset(); // Enter / click / another window
        Assert.Equal([false, false, false, false, false, true], "/table".Select(t.Type).ToArray());
        foreach (var c in "/tabx") t.Type(c);
        t.Backspace();
        foreach (var c in "l") t.Type(c);
        Assert.True(t.Type('e'));
        t.Type('/'); Assert.False(t.Type('\n')); // a control character resets
        Assert.False(t.Type('t'));
        Assert.True(SlashTrigger.IsBoundary(Vk.Enter) && SlashTrigger.IsBoundary(Vk.Left) && !SlashTrigger.IsBoundary('A'));
    }

    [Fact]
    public void WinAltT_is_no_longer_a_hotkey()
    {
        var e = new KeyEngine();
        e.Feed(Vk.LWin, true, false);
        e.Feed(Vk.LAlt, true, false);
        Assert.Equal(Decision.Pass, e.Feed('T', true, false));
    }

    [Fact]
    public void AltT_alone_still_toggles_the_title_bar()
    {
        var e = new KeyEngine();
        e.Feed(Vk.LAlt, true, false);
        Assert.Equal(new Decision(true, Act.ToggleChrome), e.Feed('T', true, false));
    }

    [Fact]
    public void The_help_table_lists_the_table_editor()
    {
        Assert.Contains(KeyEngine.Help, h => h.Keys == "type /table" && h.What.StartsWith("Table editor"));
    }
}
