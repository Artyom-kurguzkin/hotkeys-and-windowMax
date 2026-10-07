using System.Runtime.InteropServices;
using WindowManager;
using static WindowManager.Native;

namespace WindowManager.Tests;

// Real Win32 calls, but only against windows this test process creates; the user's windows are never touched.
public class Win32Tests
{
    // Wrong struct layout makes SendInput fail silently (returns 0); catch it here instead of at runtime.
    [Fact]
    public void Structs_marshal_to_x64_win32_sizes()
    {
        Assert.Equal(40, Marshal.SizeOf<INPUT>());
        Assert.Equal(24, Marshal.SizeOf<KBDLLHOOKSTRUCT>());
        Assert.Equal(32, Marshal.SizeOf<MSLLHOOKSTRUCT>());
        Assert.Equal(40, Marshal.SizeOf<MONITORINFO>());
        Assert.Equal(16, Marshal.SizeOf<RAWINPUTDEVICE>());
        Assert.Equal(16, Marshal.SizeOf<RAWINPUTDEVICELIST>());
    }

    const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;

    static IntPtr CreateTestWindow() =>
        CreateWindowEx(0, "STATIC", "wm-test", WS_OVERLAPPEDWINDOW, 100, 100, 400, 300, IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);

    [Fact]
    public void Strip_and_restore_round_trip_the_style()
    {
        var hwnd = CreateTestWindow();
        Assert.NotEqual(IntPtr.Zero, hwnd);
        try
        {
            var original = Frames.Style(hwnd);
            Assert.True(Chrome.HasCaption(original));

            Frames.Strip(hwnd);
            var stripped = Frames.Style(hwnd);
            Assert.False(Chrome.HasCaption(stripped));
            Assert.Equal(Chrome.WS_THICKFRAME, stripped & Chrome.WS_THICKFRAME);

            Frames.Unstrip(hwnd, original);
            Assert.Equal(original & Chrome.WS_CAPTION, Frames.Style(hwnd) & Chrome.WS_CAPTION);
        }
        finally { DestroyWindow(hwnd); }
    }

    [Fact]
    public void Dwm_frame_attributes_are_accepted()
    {
        var hwnd = CreateTestWindow();
        try
        {
            Assert.Equal((0, 0), Frames.SetDwmFrameless(hwnd, true));
            Assert.Equal((0, 0), Frames.SetDwmFrameless(hwnd, false));
        }
        finally { DestroyWindow(hwnd); }
    }

    // End-to-end check of the taskbar cover against real Windows maximize behavior (briefly shows a window).
    [Fact]
    public void Maximized_stripped_window_covers_monitor()
    {
        SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        var hwnd = CreateWindowEx(0, "STATIC", "wm-test", WS_OVERLAPPEDWINDOW | Focus.WS_VISIBLE, 100, 100, 400, 300,
            IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        try
        {
            Frames.Strip(hwnd);
            ShowWindow(hwnd, SW_MAXIMIZE);
            GetWindowRect(hwnd, out var window);
            var monitor = Frames.MonitorRect(hwnd);
            var target = Chrome.CoverRect(IsZoomed(hwnd), monitor, window, Frames.ClientOnScreen(hwnd));
            if (target is { } t) Frames.Move(hwnd, t); // null only when there's no taskbar on this monitor
            Assert.Equal(monitor, Frames.ClientOnScreen(hwnd));
            Assert.True(IsZoomed(hwnd));
        }
        finally { DestroyWindow(hwnd); }
    }

    [Fact]
    public void Client_rect_on_screen_sits_inside_window_rect()
    {
        var hwnd = CreateTestWindow();
        try
        {
            GetWindowRect(hwnd, out var w);
            var c = Frames.ClientOnScreen(hwnd);
            Assert.True(c.Left >= w.Left && c.Top > w.Top && c.Right <= w.Right && c.Bottom <= w.Bottom); // caption above client
        }
        finally { DestroyWindow(hwnd); }
    }
}
