using WindowManager;

namespace WindowManager.Tests;

public class LieTests
{
    [Fact]
    public void Exact_fullscreen_counts_as_filled()
    {
        var slot = new Rect(0, 0, 960, 1200);
        Assert.True(LieLogic.FilledSlot(new Rect(0, 0, 960, 1200), slot));
    }

    [Fact]
    public void A_few_pixels_off_still_counts() // Chromium's 1px auto-hide-taskbar sliver etc.
    {
        var slot = new Rect(960, 0, 1920, 1200);
        Assert.True(LieLogic.FilledSlot(new Rect(960, 0, 1920, 1199), slot));
    }

    [Fact]
    public void Full_monitor_when_slot_is_half_is_not_filled() // app ignored the fullscreen trigger
    {
        var slot = new Rect(0, 0, 960, 1200);
        Assert.False(LieLogic.FilledSlot(new Rect(0, 0, 1920, 1200), slot));
    }

    [Fact]
    public void Maximized_slot_is_the_whole_monitor()
    {
        var monitor = new Rect(0, 0, 1920, 1200);
        var window = new Rect(-8, -8, 1928, 1208); // maximized overhang
        Assert.Equal(monitor, LieLogic.SlotFor(window, monitor, zoomed: true));
    }

    [Fact]
    public void Floating_slot_is_the_window_rect()
    {
        var monitor = new Rect(0, 0, 1920, 1200);
        var window = new Rect(100, 100, 900, 700);
        Assert.Equal(window, LieLogic.SlotFor(window, monitor, zoomed: false));
    }

    [Fact]
    public void Snapped_left_half_slot_is_that_half()
    {
        var monitor = new Rect(0, 0, 1920, 1200);
        var half = new Rect(0, 0, 960, 1200);
        Assert.Equal(half, LieLogic.SlotFor(half, monitor, zoomed: false));
    }

    [Fact]
    public void AlignVisible_pushes_invisible_borders_off_so_content_meets_the_target()
    {
        // A window with 7px invisible side/bottom borders: window rect is 7px wider than its visible frame.
        var window = new Rect(953, 0, 1927, 1207);
        var frame = new Rect(960, 0, 1920, 1200); // what the user actually sees
        var target = new Rect(960, 0, 1920, 1200); // the right half we want the content to fill
        var placed = LieLogic.AlignVisible(window, frame, target);
        // Placing the window there makes its VISIBLE frame land exactly on the target (borders hang off).
        Assert.Equal(new Rect(953, 0, 1927, 1207), placed);
    }

    [Fact]
    public void AlignVisible_is_identity_for_a_borderless_window()
    {
        var r = new Rect(0, 0, 960, 1200);
        Assert.Equal(r, LieLogic.AlignVisible(r, r, r)); // frame == window: nothing to compensate
    }

    // ---- probe verdict: the false "not-capable" that sidelined Edge for a session ----
    static readonly Rect Monitor = new(0, 0, 1920, 1200);

    [Fact]
    public void Probe_filled_the_slot_is_capable()
    {
        var slot = new Rect(0, 0, 960, 1200);
        Assert.Equal(LieLogic.Probe.Capable, LieLogic.ProbeVerdict(new Rect(-3, 0, 1923, 745), new Rect(0, 0, 960, 1200), slot, Monitor));
    }

    [Fact]
    public void Probe_fullscreened_to_the_real_monitor_is_a_miss_not_incapable() // Edge: lie not live yet
    {
        var slot = new Rect(-3, 0, 1923, 745);
        Assert.Equal(LieLogic.Probe.LieMissed, LieLogic.ProbeVerdict(new Rect(-3, 0, 1923, 745), Monitor, slot, Monitor));
    }

    [Fact]
    public void Probe_unchanged_window_is_not_capable() // Word: F11 did nothing
    {
        var before = new Rect(-11, -11, 1931, 1211);
        Assert.Equal(LieLogic.Probe.NotCapable, LieLogic.ProbeVerdict(before, before, Monitor, Monitor));
    }

    [Fact]
    public void Probe_landed_somewhere_odd_is_its_own_verdict_never_a_judgement_of_the_program()
    {
        var before = new Rect(100, 100, 900, 700);
        Assert.Equal(LieLogic.Probe.Landed, LieLogic.ProbeVerdict(before, new Rect(50, 50, 500, 500), Monitor, Monitor));
    }

    // ---- clamp: an overhanging non-maximized frame must never become an off-screen slot ----
    [Fact]
    public void Clamp_pulls_an_overhanging_frame_onto_the_monitor() // Vivaldi parked at (-13,-10) 1946 wide
    {
        Assert.Equal(Monitor, LieLogic.Clamp(new Rect(-13, -10, 1933, 1213), Monitor));
    }

    [Fact]
    public void Clamp_leaves_an_onscreen_frame_alone()
    {
        var half = new Rect(960, 0, 1920, 1200);
        Assert.Equal(half, LieLogic.Clamp(half, Monitor));
    }

    // ---- fullscreen detector: F11 toggles, so "is it fullscreen NOW" must be observable, never assumed ----
    [Fact]
    public void App_fullscreen_is_an_exact_monitor_fit()
    {
        Assert.True(LieLogic.IsAppFullscreen(Monitor, Monitor));
    }

    [Fact]
    public void Chromium_one_px_short_at_the_bottom_still_counts() // auto-hide taskbar sliver
    {
        Assert.True(LieLogic.IsAppFullscreen(new Rect(0, 0, 1920, 1199), Monitor));
    }

    [Fact]
    public void Maximized_overhang_is_NOT_fullscreen() // Edge's -12 frame; the toggle bug hinged on this
    {
        Assert.False(LieLogic.IsAppFullscreen(new Rect(-12, -12, 1932, 1212), Monitor));
    }

    [Fact]
    public void Edge_restore_rect_is_NOT_fullscreen() // what every wrong-way F11 landed on
    {
        Assert.False(LieLogic.IsAppFullscreen(new Rect(-12, 0, 1932, 754), Monitor));
    }

    [Fact]
    public void Fullscreen_at_a_lied_slot_is_detected_against_that_slot()
    {
        var left = new Rect(0, 0, 960, 1200);
        Assert.True(LieLogic.IsAppFullscreen(new Rect(0, 0, 960, 1199), left));
        Assert.False(LieLogic.IsAppFullscreen(new Rect(0, 0, 960, 1199), Monitor));
    }
}
