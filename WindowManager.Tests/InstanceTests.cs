using WindowManager;
using static WindowManager.Native;

namespace WindowManager.Tests;

// Scenarios from the single-instance / recovery requirements in specs/diagnostics.
public class InstanceTests
{
    [Fact]
    public void Masked_release_sends_mask_before_the_release() =>
        Assert.Equal([new(Vk.Mask, false), new(Vk.Mask, true), new(Vk.LWin, true)], KeyEngine.MaskedRelease(Vk.LWin));

    [Fact]
    public void State_round_trips_through_the_file()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory().FullName, "state.json");
        State.Entry[] entries = [new(0x1234, 0x14CF0000, [0, -60, 1920, 1200], 2), new(0x5678, 0x14CF0000, null)];
        State.Save(path, entries);
        var back = State.Load(path);
        Assert.Equal(2, back.Length);
        Assert.Equal(entries[0].Slot, back[0].Slot);
        Assert.Equal(2u, back[0].Backdrop);
        Assert.Null(back[1].Backdrop);
        Assert.Null(back[1].Slot);
        Assert.Equal(0x14CF0000u, back[1].OriginalStyle);
    }

    [Fact]
    public void Missing_or_corrupt_state_is_empty()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        Assert.Empty(State.Load(Path.Combine(dir, "none.json")));
        File.WriteAllText(Path.Combine(dir, "bad.json"), "{not json");
        Assert.Empty(State.Load(Path.Combine(dir, "bad.json")));
    }

    // A terminated instance left a window stripped, stretched and clipped; recovery puts it back.
    [Fact]
    public void Recovery_undoes_a_dead_instances_changes()
    {
        const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
        SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        var hwnd = CreateWindowEx(0, "STATIC", "wm-test", WS_OVERLAPPEDWINDOW, 300, 300, 500, 400,
            IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        try
        {
            var original = Frames.Style(hwnd);
            GetWindowRect(hwnd, out var slot);
            var (target, region) = Crop.Plan(slot, Frames.ClientOnScreen(hwnd), 40);
            Frames.Strip(hwnd);
            Frames.SetRegion(hwnd, region);
            Frames.MoveUnclamped(hwnd, target);

            Assert.Equal(1, State.Recover([new State.Entry(hwnd, original, State.ToArray(slot))]));

            Assert.True(Chrome.HasCaption(Frames.Style(hwnd)));
            Assert.Equal(0, GetWindowRgnBox(hwnd, out _));
            GetWindowRect(hwnd, out var after);
            Assert.Equal(slot, after);
        }
        finally { DestroyWindow(hwnd); }
    }

    [Fact]
    public void Recovery_skips_windows_that_no_longer_exist() =>
        Assert.Equal(0, State.Recover([new State.Entry(0x7FFFFFF0, 0x14CF0000, null)]));
}
