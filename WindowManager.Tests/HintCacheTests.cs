namespace WindowManager.Tests;

// When an earlier hint scan may be shown, when to refresh it, and when a refreshed one may replace the badges on screen.
public class HintCacheTests
{
    static readonly Rect R1 = new(0, 0, 1920, 1200), R2 = new(100, 100, 800, 600);
    static List<(long, Rect)> Win(params (long, Rect)[] w) => [.. w];

    [Fact] public void A_recent_scan_of_the_same_windows_is_usable() =>
        Assert.True(HintCache.Usable(1500, true, Win((1, R1)), Win((1, R1))));

    [Fact] public void An_old_scan_is_not() =>
        Assert.False(HintCache.Usable(HintCache.MaxAgeMs + 1, true, Win((1, R1)), Win((1, R1))));

    [Fact] public void Another_window_having_the_focus_is_not() =>
        Assert.False(HintCache.Usable(100, false, Win((1, R1)), Win((1, R1))));

    [Fact] public void A_window_that_moved_or_resized_is_not() =>
        Assert.False(HintCache.Usable(100, true, Win((1, R1)), Win((1, new Rect(0, 0, 960, 1200)))));

    [Fact] public void A_new_window_on_screen_is_not() =>
        Assert.False(HintCache.Usable(100, true, Win((1, R1)), Win((2, R2), (1, R1))));

    [Fact] public void A_window_that_went_away_is_not() =>
        Assert.False(HintCache.Usable(100, true, Win((1, R1), (2, R2)), Win((1, R1))));

    [Fact] public void The_order_of_windows_matters_because_it_decides_the_letters() =>
        Assert.False(HintCache.Usable(100, true, Win((1, R1), (2, R2)), Win((2, R2), (1, R1))));

    [Fact] public void Nothing_scanned_is_never_usable() =>
        Assert.False(HintCache.Usable(0, true, Win(), Win()));

    [Fact] public void A_clock_that_went_backwards_is_not_trusted() =>
        Assert.False(HintCache.Usable(-5, true, Win((1, R1)), Win((1, R1))));

    [Theory]
    [InlineData(5000, 1000, true)]    // working, last scan is old: refresh
    [InlineData(1000, 1000, false)]   // working, last scan is fresh: leave it
    [InlineData(5000, 60000, false)]  // gone idle: do not spend CPU (or wake Chromium's accessibility) for nobody
    [InlineData(-1, 1000, true)]      // never scanned, working: scan
    public void Refresh_only_while_the_user_works_and_the_scan_has_aged(long age, long sinceInput, bool want) =>
        Assert.Equal(want, HintCache.WantsRefresh(age, sinceInput));

    [Fact] public void A_changed_page_redraws_the_badges_if_nothing_was_typed() =>
        Assert.True(HintCache.ShouldRedraw(true, [R1], [R1, R2]));

    [Fact] public void An_unchanged_page_does_not_redraw() =>
        Assert.False(HintCache.ShouldRedraw(true, [R1, R2], [R1, R2]));

    [Fact] public void Letters_never_change_under_the_users_fingers() =>
        Assert.False(HintCache.ShouldRedraw(false, [R1], [R1, R2]));
}
