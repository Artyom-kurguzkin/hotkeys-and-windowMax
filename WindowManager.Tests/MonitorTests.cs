using System.Runtime.InteropServices;
using WindowManager;

namespace WindowManager.Tests;

public class MoveToMonitorTests
{
    readonly KeyEngine e = new();

    // Measured sequence: holding the Copilot key repeats LWin, LShift, F23 every ~30ms; release sends F23, LShift, LWin up.
    void CopilotDown(int repeats = 3)
    {
        for (var i = 0; i < repeats; i++)
        {
            e.Feed(Vk.LWin, true, false);
            e.Feed(Vk.LShift, true, false);
            Assert.True(e.Feed(Vk.F23, true, false).Suppress); // Copilot itself never launches
        }
    }

    [Theory]
    [InlineData('1', 1)]
    [InlineData('3', 3)]
    [InlineData('9', 9)]
    public void Copilot_plus_digit_moves_to_that_monitor(int key, int monitor)
    {
        CopilotDown();
        Assert.Equal(new Decision(true, Act.MoveToMonitor, Arg: monitor), e.Feed(key, true, false));
        Assert.True(e.Feed(key, false, false).Suppress);
    }

    [Fact]
    public void Copilot_release_opens_no_start_menu()
    {
        CopilotDown();
        Assert.True(e.Feed(Vk.F23, false, false).Suppress);
        e.Feed(Vk.LShift, false, false);
        Assert.True(e.Feed(Vk.LWin, false, false).MaskFirst); // Win release masked: no Start menu
    }

    [Fact]
    public void Digit_after_Copilot_released_is_typed_normally()
    {
        CopilotDown();
        e.Feed(Vk.F23, false, false);
        e.Feed(Vk.LShift, false, false);
        e.Feed(Vk.LWin, false, false);
        Assert.Equal(Decision.Pass, e.Feed('1', true, false));
    }

    [Fact]
    public void Missed_F23_release_is_cleared_by_Win_release()
    {
        CopilotDown();
        e.Feed(Vk.LShift, false, false);
        e.Feed(Vk.LWin, false, false); // F23 up never arrived
        Assert.Equal(Decision.Pass, e.Feed('2', true, false));
    }

    [Theory]
    [InlineData(Vk.LWin, Vk.LAlt)]
    [InlineData(Vk.RAlt, Vk.RWin)]
    public void WinAlt_plus_digit_moves_to_that_monitor(int a, int b)
    {
        e.Feed(a, true, false);
        e.Feed(b, true, false);
        Assert.Equal(new Decision(true, Act.MoveToMonitor, Arg: 2), e.Feed('2', true, false));
        Assert.True(e.Feed('2', false, false).Suppress);
        Assert.True(e.Feed(a, false, false).MaskFirst);         // no Start menu / menu bar afterwards
        Assert.NotEqual(Act.ToggleReveal, e.Feed(b, false, false).Act); // the digit cancelled the reveal tap
    }

    [Fact]
    public void WinAltShift_plus_digit_is_not_a_move() =>
        Assert.Equal(Decision.Pass, Press('3', Vk.LWin, Vk.LAlt, Vk.LShift));

    Decision Press(int key, params int[] mods)
    {
        foreach (var m in mods) e.Feed(m, true, false);
        return e.Feed(key, true, false);
    }

    [Fact]
    public void Zero_is_not_a_monitor()
    {
        CopilotDown();
        Assert.NotEqual(Act.MoveToMonitor, e.Feed('0', true, false).Act);
    }

    [Theory]
    [InlineData(@"\\.\DISPLAY1", 1)]
    [InlineData(@"\\.\DISPLAY12", 12)]
    public void Monitor_number_comes_from_the_device_name(string device, int n) => Assert.Equal(n, Monitors.Number(device));

    [Fact]
    public void Device_without_number_has_none() => Assert.Null(Monitors.Number("weird"));

    // Laptop panel 1920x1200 below, 2560x1440 monitor above (this machine's layout).
    static readonly Rect Laptop = new(0, 0, 1920, 1200);
    static readonly Rect Upper = new(-640, -1440, 1920, 0);

    [Fact]
    public void Window_keeps_its_relative_place_and_size()
    {
        var leftHalf = new Rect(0, 0, 960, 1200);
        Assert.Equal(new Rect(-640, -1440, 640, 0), Monitors.Map(leftHalf, Laptop, Upper));
    }

    [Fact]
    public void Mapped_window_stays_inside_the_target()
    {
        var spillsOver = new Rect(-7, 0, 1927, 1207); // snapped window with invisible borders
        var m = Monitors.Map(spillsOver, Laptop, Upper);
        Assert.True(m.Left >= Upper.Left && m.Top >= Upper.Top && m.Right <= Upper.Right && m.Bottom <= Upper.Bottom);
    }

    [Fact]
    public void Placement_structs_match_win32_sizes()
    {
        Assert.Equal(44, Marshal.SizeOf<Native.WINDOWPLACEMENT>());
        Assert.Equal(104, Marshal.SizeOf<Native.MONITORINFOEX>());
    }

    [Fact]
    public void Cropped_rect_maps_back_to_its_slot()
    {
        var t = new CropTracker();
        var slot = new Rect(100, 100, 900, 700);
        var step = t.OnLocation(slot, slot, 40);
        Assert.Equal(slot, t.SlotFor(step.Target));
        Assert.Null(t.SlotFor(new Rect(1, 2, 3, 4)));
    }

    [Fact]
    public void All_monitors_have_numbers() // runs against this machine's real displays (read-only)
    {
        var monitors = Frames.AllMonitors();
        Assert.NotEmpty(monitors);
        Assert.All(monitors, m => Assert.NotNull(Monitors.Number(m.Device)));
        Assert.Single(monitors, m => m.Primary);
    }
}
