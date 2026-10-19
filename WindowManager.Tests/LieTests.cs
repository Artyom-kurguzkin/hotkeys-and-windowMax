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
}
