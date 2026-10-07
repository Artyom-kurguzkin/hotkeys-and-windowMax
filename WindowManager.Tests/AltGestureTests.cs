using WindowManager;

namespace WindowManager.Tests;

public class AltGestureTests
{
    readonly KeyEngine e = new();

    Decision Tap(int vk, uint at)
    {
        e.Feed(vk, true, false, at);
        return e.Feed(vk, false, false, at + 60);
    }

    [Fact]
    public void WinAltX_closes_the_active_window()
    {
        e.Feed(Vk.LWin, true, false);
        e.Feed(Vk.LAlt, true, false);
        Assert.Equal(new Decision(true, Act.Close), e.Feed('X', true, false));
        Assert.Equal(Chrome.SC_CLOSE, Chrome.WindowCommand(Act.Close, false)); // what Alt+F4 sends
    }

    [Fact]
    public void Triple_Alt_tap_highlights_the_window()
    {
        Assert.Equal(Decision.Pass, Tap(Vk.LAlt, 1000));
        Assert.Equal(Decision.Pass, Tap(Vk.LAlt, 1250));
        Assert.Equal(new Decision(false, Act.HighlightWindow, MaskFirst: true), Tap(Vk.LAlt, 1500));
    }

    [Fact]
    public void Either_Alt_counts() // mixing left and right Alt taps
    {
        Tap(Vk.LAlt, 1000);
        Tap(Vk.RAlt, 1250);
        Assert.Equal(Act.HighlightWindow, Tap(Vk.LAlt, 1500).Act);
    }

    [Fact]
    public void Slow_taps_dont_count()
    {
        Tap(Vk.LAlt, 1000);
        Tap(Vk.LAlt, 1250);
        Assert.Equal(Decision.Pass, Tap(Vk.LAlt, 2000)); // > 400ms after the previous tap: starts over
    }

    [Fact]
    public void Alt_used_in_a_chord_breaks_the_sequence()
    {
        Tap(Vk.LAlt, 1000);
        Tap(Vk.LAlt, 1250);
        e.Feed(Vk.LAlt, true, false, 1400);
        e.Feed(0x09 /* Tab */, true, false, 1420); // Alt+Tab
        e.Feed(0x09 /* Tab */, false, false, 1440);
        Assert.NotEqual(Act.HighlightWindow, e.Feed(Vk.LAlt, false, false, 1460).Act);
    }

    [Fact]
    public void Fourth_tap_starts_a_new_sequence()
    {
        Tap(Vk.LAlt, 1000); Tap(Vk.LAlt, 1200); Tap(Vk.LAlt, 1400);
        Assert.Equal(Decision.Pass, Tap(Vk.LAlt, 1600));
    }

    [Fact]
    public void TrackPoint_scroll_with_Alt_is_not_a_tap()
    {
        Tap(Vk.LAlt, 1000);
        Tap(Vk.LAlt, 1200);
        e.Feed(Vk.LAlt, true, false, 1300);
        e.MarkChordUsed(); // scrolled while holding Alt
        Assert.NotEqual(Act.HighlightWindow, e.Feed(Vk.LAlt, false, false, 1350).Act);
    }

    [Fact]
    public void Both_Alts_show_the_hotkey_list_once()
    {
        e.Feed(Vk.LAlt, true, false);
        Assert.Equal(new Decision(false, Act.ShowHelp), e.Feed(Vk.RAlt, true, false));
        Assert.Equal(Decision.Pass, e.Feed(Vk.RAlt, true, false)); // auto-repeat: not again
        Assert.True(e.Feed(Vk.RAlt, false, false).MaskFirst);    // no menu bar afterwards
        Assert.True(e.Feed(Vk.LAlt, false, false).MaskFirst);
    }

    [Fact]
    public void Pressing_both_Alts_again_fires_again() // the window toggles: open, then close
    {
        for (var i = 0; i < 2; i++)
        {
            e.Feed(Vk.LAlt, true, false);
            Assert.Equal(Act.ShowHelp, e.Feed(Vk.RAlt, true, false).Act);
            e.Feed(Vk.RAlt, false, false);
            e.Feed(Vk.LAlt, false, false);
        }
    }

    // Real window, created by this test process only.
    [Fact]
    public void Hotkey_window_toggles_open_and_closed()
    {
        static bool WaitFor(Func<bool> cond) { for (var i = 0; i < 100 && !cond(); i++) Thread.Sleep(50); return cond(); }
        Assert.True(HelpWindow.Toggle(IntPtr.Zero));                  // opens
        Assert.True(WaitFor(() => HelpWindow.IsShowing));
        Assert.False(HelpWindow.Toggle(IntPtr.Zero));                 // second press closes it
        Assert.True(WaitFor(() => !HelpWindow.IsOpen));
        Assert.True(HelpWindow.Toggle(IntPtr.Zero));                  // and it can open again
        Assert.True(WaitFor(() => HelpWindow.IsShowing));
        HelpWindow.Toggle(IntPtr.Zero);
        Assert.True(WaitFor(() => !HelpWindow.IsOpen));
    }

    [Fact]
    public void Both_Alts_is_not_part_of_a_triple_tap()
    {
        Tap(Vk.LAlt, 1000);
        Tap(Vk.LAlt, 1200);
        e.Feed(Vk.LAlt, true, false, 1300);
        e.Feed(Vk.RAlt, true, false, 1310);
        e.Feed(Vk.RAlt, false, false, 1350);
        Assert.NotEqual(Act.HighlightWindow, e.Feed(Vk.LAlt, false, false, 1360).Act);
    }

    [Fact]
    public void Help_lists_every_binding_key()
    {
        var text = string.Join("\n", KeyEngine.Help.Select(h => h.Keys));
        foreach (var b in KeyEngine.Bindings)
        {
            var key = b.Vk switch { Vk.OemOpen => "[", Vk.OemClose => "]", Vk.PgUp => "PageUp", Vk.PgDn => "PageDown", _ => ((char)b.Vk).ToString() };
            Assert.Contains(key, text);
        }
        Assert.Contains("Win+Alt+X", text);
    }
}
