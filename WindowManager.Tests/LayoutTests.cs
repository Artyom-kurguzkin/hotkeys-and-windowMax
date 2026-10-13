using WindowManager;

namespace WindowManager.Tests;

public class UwpCropTests
{
    [Fact]
    public void Uwp_frame_is_not_cropped() => Assert.False(Crop.ShouldCrop(true, Chrome.UwpFrameClass));

    [Theory]
    [InlineData("Chrome_WidgetWin_1")] // Chromium / Electron
    [InlineData("Outlook Host")]       // packaged Win32 app: still cropped
    public void Other_self_drawn_apps_are_still_cropped(string cls) => Assert.True(Crop.ShouldCrop(true, cls));

    [Fact]
    public void Classic_window_is_never_cropped() => Assert.False(Crop.ShouldCrop(false, "Chrome_WidgetWin_1"));
}

public class SleepAndDisplayTests
{
    // This machine's layout: laptop panel, 4K above, portrait on the right.
    static readonly Display Laptop = new(@"\\.\DISPLAY1", new Rect(0, 0, 1920, 1200));
    static readonly Display Upper = new(@"\\.\DISPLAY3", new Rect(-960, -2160, 2880, 0));
    static readonly Display Portrait = new(@"\\.\DISPLAY2", new Rect(2880, -2186, 4500, 694));
    static readonly Display[] All = [Laptop, Upper, Portrait];

    static readonly LayoutRecord OnUpperLeftHalf = new(Upper, new Rect(-960, -2160, 960, 0), false, new Rect(0, -2000, 1000, -1000));
    static readonly LayoutRecord MaximizedOnUpper = new(Upper, new Rect(-971, -2171, 2891, 11), true, new Rect(-500, -2000, 500, -1000));

    [Fact]
    public void Signature_ignores_enumeration_order() =>
        Assert.Equal(Layout.Signature([Laptop, Upper, Portrait]), Layout.Signature([Portrait, Laptop, Upper]));

    [Fact]
    public void Monitor_dropping_out_changes_the_signature() =>
        Assert.NotEqual(Layout.Signature(All), Layout.Signature([Laptop, Portrait]));

    [Fact]
    public void Monitor_moved_or_resized_changes_the_signature() =>
        Assert.NotEqual(Layout.Signature(All), Layout.Signature([Laptop, Upper with { Bounds = new Rect(0, -2160, 3840, 0) }, Portrait]));

    [Fact]
    public void Clock_jump_means_the_machine_slept()
    {
        Assert.True(Layout.SleptBetween(1_000, 1_000 + 8 * 3_600_000)); // overnight
        Assert.False(Layout.SleptBetween(1_000, 1_700));                 // a normal 700ms tick
        Assert.False(Layout.SleptBetween(1_000, 6_000));                 // a slow tick, not a sleep
    }

    [Fact]
    public void Window_goes_back_when_its_monitor_returns() =>
        Assert.Equal(OnUpperLeftHalf, Layout.RestoreTarget(OnUpperLeftHalf, All));

    [Fact]
    public void Window_stays_put_when_its_monitor_is_gone() =>
        Assert.Null(Layout.RestoreTarget(OnUpperLeftHalf, [Laptop, Portrait]));

    [Fact]
    public void Window_stays_put_when_its_monitor_came_back_different() // e.g. another resolution or arrangement
        => Assert.Null(Layout.RestoreTarget(OnUpperLeftHalf, [Laptop, Upper with { Bounds = new Rect(-1920, -2160, 1920, 0) }, Portrait]));

    [Fact]
    public void Untracked_window_stays_put() => Assert.Null(Layout.RestoreTarget(null, All));

    [Fact]
    public void Window_already_in_place_is_not_moved()
    {
        Assert.True(Layout.AlreadyThere(OnUpperLeftHalf, Upper.Device, false, OnUpperLeftHalf.Slot));
        Assert.True(Layout.AlreadyThere(MaximizedOnUpper, Upper.Device, true, new Rect(1, 2, 3, 4))); // maximized: any rect
    }

    [Fact]
    public void Window_evacuated_by_Windows_is_moved_back()
    {
        Assert.False(Layout.AlreadyThere(OnUpperLeftHalf, Laptop.Device, false, new Rect(0, 0, 1920, 1200))); // other monitor
        Assert.False(Layout.AlreadyThere(OnUpperLeftHalf, Upper.Device, false, new Rect(-900, -2100, 1000, 0))); // shifted
        Assert.False(Layout.AlreadyThere(MaximizedOnUpper, Upper.Device, false, MaximizedOnUpper.Slot)); // un-maximized
    }

    // The recorded slot of a cropped window is Windows' rect, not our stretched one, so re-cropping after the wake
    // starts from the right place instead of cropping a second time.
    [Fact]
    public void Recorded_slot_is_never_the_stretched_crop_rect()
    {
        var t = new CropTracker();
        var slot = new Rect(-960, -2160, 960, 0);
        var stretched = t.OnLocation(slot, slot with { Left = -953, Right = 953, Bottom = -7 }, 30).Target;
        Assert.NotEqual(slot, stretched);
        Assert.Equal(slot, t.SlotFor(stretched) ?? stretched);
    }

    // Measured live: a floating Edge window laid out at (200,150 1000x700), cropped to window (190,90 1020x770) with
    // clip (10,60 1000x700). The record must be the laid-out rect, not the border-widened slot (190,150 1020x710).
    [Fact]
    public void Recorded_floating_window_is_its_visible_area()
    {
        var window = new Rect(190, 90, 1210, 860);
        var region = new Rect(10, 60, 1010, 760);
        Assert.Equal(new Rect(200, 150, 1200, 850), Crop.VisibleOnScreen(window, region));
    }

    [Fact]
    public void Rect_offset_converts_workspace_to_screen() =>
        Assert.Equal(new Rect(10, 20, 110, 220), new Rect(0, 0, 100, 200).Offset(10, 20));
}
