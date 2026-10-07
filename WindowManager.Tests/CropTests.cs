using WindowManager;
using static WindowManager.Native;

namespace WindowManager.Tests;

// Scenarios from openspec/changes/add-chrome-crop/specs/chrome-crop/spec.md.
public class ChromeCropTests
{
    // Edge at 144 DPI as measured in the spike: maximized window overhangs 10px, client starts at the monitor top.
    static readonly Rect MaxWindow = new(-10, -10, 1930, 1210);
    static readonly Rect MaxClient = new(0, 0, 1920, 1198);
    static readonly Rect FloatWindow = new(200, 200, 1200, 800);
    static readonly Rect FloatClient = new(210, 200, 1190, 790); // l/r/b borders 10px, no caption

    static Rect ToScreen(Rect target, Rect region) =>
        new(target.Left + region.Left, target.Top + region.Top, target.Left + region.Right, target.Top + region.Bottom);

    [Fact]
    public void Chromium_window_is_detected()
    {
        Assert.True(Crop.IsSelfDrawn(MaxWindow, MaxClient, 144));
        Assert.True(Crop.IsSelfDrawn(FloatWindow, FloatClient, 144));
    }

    [Fact]
    public void Classic_window_is_not_cropped() // 45px caption at 144 DPI above the client
        => Assert.False(Crop.IsSelfDrawn(new Rect(-11, -11, 1931, 1211), new Rect(0, 45, 1920, 1200), 144));

    [Fact]
    public void App_fullscreen_is_not_cropped()
    {
        var monitor = new Rect(0, 0, 1920, 1200);
        Assert.True(Crop.IsAppFullscreen(monitor, monitor));        // F11: exactly the monitor
        Assert.True(Crop.IsAppFullscreen(monitor with { Bottom = 1199 }, monitor)); // Chromium/VS Code: 1px left for auto-hide taskbar
        Assert.False(Crop.IsAppFullscreen(MaxWindow, monitor));     // maximized: frame overhang
        Assert.False(Crop.IsAppFullscreen(MaxWindow with { Top = -70 }, monitor)); // already cropped
    }

    [Fact]
    public void Window_placed_by_Windows_is_cropped()
    {
        var step = new CropTracker().OnLocation(MaxWindow, MaxClient, 60);
        Assert.Equal(CropKind.Apply, step.Kind);
        Assert.Equal(MaxWindow with { Top = -70 }, step.Target);
    }

    [Fact]
    public void Clip_covers_only_content()
    {
        var (target, region) = Crop.Plan(MaxWindow, MaxClient, 60);
        Assert.Equal(new Rect(10, 70, 1930, 1268), region); // target-window coords: borders and the 60px strip dropped
        Assert.Equal(MaxClient, ToScreen(target, region));  // on screen: exactly the original content area
    }

    [Fact]
    public void Floating_clip_maps_back_to_client()
    {
        var (target, region) = Crop.Plan(FloatWindow, FloatClient, 60);
        Assert.Equal(FloatClient, ToScreen(target, region));
    }

    [Fact]
    public void Own_move_is_not_cropped_again()
    {
        var t = new CropTracker();
        var step = t.OnLocation(MaxWindow, MaxClient, 60);
        Assert.Equal(CropStep.None, t.OnLocation(step.Target, MaxClient with { Top = -60 }, 60));
    }

    [Fact]
    public void Restore_to_a_previously_cropped_rect_is_not_double_cropped()
    {
        var t = new CropTracker();
        var floatCrop = t.OnLocation(FloatWindow, FloatClient, 60);                    // floating, cropped
        t.OnLocation(MaxWindow, MaxClient, 60);                                         // maximized, cropped
        var back = t.OnLocation(floatCrop.Target, FloatClient with { Top = 140 }, 60);  // Windows restores our cropped normal rect
        Assert.Equal(CropKind.RegionOnly, back.Kind);
        Assert.Equal(floatCrop.Region, back.Region);
    }

    [Fact]
    public void Refused_crop_is_not_retried()
    {
        var t = new CropTracker();
        t.OnLocation(MaxWindow, MaxClient, 60);
        Assert.Equal(CropStep.None, t.OnLocation(MaxWindow, MaxClient, 60)); // app snapped back to the slot
    }

    [Fact]
    public void Height_scales_with_DPI()
    {
        Assert.Equal(60, Crop.Pixels(40, 144));
        Assert.Equal(40, Crop.Pixels(40, 96));
    }

    [Fact]
    public void Unknown_app_uses_default()
    {
        Assert.Equal(Crop.DefaultDip, Crop.DipFor("someapp", new Dictionary<string, int>()));
        Assert.Equal(40, Crop.DipFor("MSEDGE", new Dictionary<string, int>())); // process names are case-insensitive
    }

    [Fact]
    public void Tuned_value_persists() => Assert.Equal(44, Crop.DipFor("msedge", new Dictionary<string, int> { ["msedge"] = 44 }));

    [Fact]
    public void Crop_more() => Assert.Equal(42, Crop.Tune(40, Crop.StepDip));

    [Fact]
    public void Crop_never_negative() => Assert.Equal(0, Crop.Tune(0, -Crop.StepDip));

    [Fact]
    public void Retune_recrops_from_the_same_slot()
    {
        var t = new CropTracker();
        t.OnLocation(MaxWindow, MaxClient, 60);
        Assert.Equal(MaxWindow with { Top = -73 }, t.Replan(63).Target);
    }

    [Fact]
    public void Quit_uncrops()
    {
        var t = new CropTracker();
        t.OnLocation(MaxWindow, MaxClient, 60);
        Assert.Equal(MaxWindow, t.Uncrop());
        Assert.Null(t.Uncrop()); // nothing left to undo
        Assert.Equal(CropKind.Apply, t.OnLocation(MaxWindow, MaxClient, 60).Kind); // hiding again crops afresh
    }

    [Fact]
    public void ApplicationFrameWindow_is_manageable()
    {
        const uint uwp = 0x95CF0000; // measured: WS_POPUP | WS_VISIBLE | WS_OVERLAPPEDWINDOW
        Assert.True(Chrome.IsManageable(uwp, 0x00200100, Chrome.UwpFrameClass));
        Assert.False(Chrome.IsManageable(uwp, 0x00200100, "SomePopup"));
    }
}

public class RevealTapTests
{
    readonly KeyEngine e = new();

    [Theory]
    [InlineData(Vk.LWin, Vk.LAlt, Vk.LAlt)]
    [InlineData(Vk.LWin, Vk.LAlt, Vk.LWin)]
    [InlineData(Vk.LAlt, Vk.RWin, Vk.RWin)]
    public void WinAlt_tap_reveals(int first, int second, int releaseFirst)
    {
        e.Feed(first, true, false);
        e.Feed(second, true, false);
        Assert.Equal(new Decision(false, Act.ToggleReveal, MaskFirst: true), e.Feed(releaseFirst, false, false));
        var other = releaseFirst == first ? second : first;
        Assert.True(e.Feed(other, false, false).MaskFirst); // the remaining key's release is masked too
    }

    [Fact]
    public void WinAlt_with_another_key_is_not_a_tap()
    {
        e.Feed(Vk.LWin, true, false);
        e.Feed(Vk.LAlt, true, false);
        e.Feed('D', true, false);
        e.Feed('D', false, false);
        Assert.NotEqual(Act.ToggleReveal, e.Feed(Vk.LAlt, false, false).Act);
    }

    [Fact]
    public void WinAltShift_is_not_a_tap()
    {
        e.Feed(Vk.LWin, true, false);
        e.Feed(Vk.LAlt, true, false);
        e.Feed(Vk.LShift, true, false);
        Assert.NotEqual(Act.ToggleReveal, e.Feed(Vk.LAlt, false, false).Act);
    }

    [Fact]
    public void Second_tap_hides_again()
    {
        for (var i = 0; i < 2; i++)
        {
            e.Feed(Vk.LWin, true, false);
            e.Feed(Vk.LAlt, true, false);
            Assert.Equal(Act.ToggleReveal, e.Feed(Vk.LAlt, false, false).Act);
            e.Feed(Vk.LWin, false, false);
        }
    }

    [Fact]
    public void Alt_alone_is_not_a_tap()
    {
        e.Feed(Vk.LAlt, true, false);
        Assert.Equal(Decision.Pass, e.Feed(Vk.LAlt, false, false));
    }

    [Theory]
    [InlineData(Vk.PgDn, Act.CropMore)]
    [InlineData(Vk.PgUp, Act.CropLess)]
    public void WinAltPage_tunes_crop(int key, Act act)
    {
        e.Feed(Vk.LWin, true, false);
        e.Feed(Vk.LAlt, true, false);
        Assert.Equal(new Decision(true, act), e.Feed(key, true, false));
        Assert.True(e.Feed(Vk.LWin, false, false).MaskFirst); // no Start menu afterwards
    }
}

public class CropWin32Tests
{
    const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;

    // Crops a window the test owns (a classic captioned one: this checks the mechanics, not the detection).
    [Fact]
    public void Crop_moves_window_up_and_clips_the_strip()
    {
        SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        var hwnd = CreateWindowEx(0, "STATIC", "wm-test", WS_OVERLAPPEDWINDOW, 300, 300, 500, 400,
            IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        try
        {
            GetWindowRect(hwnd, out var window);
            var (target, region) = Crop.Plan(window, Frames.ClientOnScreen(hwnd), 40);

            Frames.SetRegion(hwnd, region);
            Frames.MoveUnclamped(hwnd, target);

            GetWindowRect(hwnd, out var after);
            Assert.Equal(target, after);
            Assert.NotEqual(0, GetWindowRgnBox(hwnd, out var box));
            Assert.Equal(region, box);

            Frames.SetRegion(hwnd, null);
            Assert.Equal(0, GetWindowRgnBox(hwnd, out _)); // ERROR = no region
        }
        finally { DestroyWindow(hwnd); }
    }
}
