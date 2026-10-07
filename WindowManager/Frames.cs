using static WindowManager.Native;

namespace WindowManager;

// Win32 operations on another window's frame. Decisions come from Chrome (Logic.cs); this only applies them.
// Public so Win32Tests can run them against a window the test owns.
public static class Frames
{
    public static uint Style(IntPtr hwnd) => (uint)GetWindowLong(hwnd, GWL_STYLE);
    public static uint ExStyle(IntPtr hwnd) => (uint)GetWindowLong(hwnd, GWL_EXSTYLE);

    // ponytail: SetWindowLong sends WM_STYLECHANGING synchronously, so a hung app stalls us here;
    // move style changes to a worker thread if that ever shows up in the log.
    public static bool SetStyle(IntPtr hwnd, uint style)
    {
        if (SetWindowLong(hwnd, GWL_STYLE, (int)style) == 0 && Style(hwnd) != style) { Log.Win32($"SetWindowLong 0x{hwnd:X}"); return false; }
        if (!SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_FRAMECHANGED | SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_ASYNCWINDOWPOS)
            && IsWindow(hwnd)) // short-lived windows can close between being shown and stripped: not an error
            Log.Win32($"SetWindowPos(framechanged) 0x{hwnd:X}");
        return true;
    }

    // Returns the two HRESULTs (border color, corner preference); 0 = S_OK.
    public static (int Border, int Corner) SetDwmFrameless(IntPtr hwnd, bool frameless)
    {
        var color = frameless ? DWMWA_COLOR_NONE : DWMWA_COLOR_DEFAULT;
        var corner = frameless ? DWMWCP_DONOTROUND : DWMWCP_DEFAULT;
        var hrBorder = DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref color, sizeof(uint));
        var hrCorner = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(uint));
        if (hrBorder != 0 || hrCorner != 0) Log.Info($"DwmSetWindowAttribute 0x{hwnd:X} border=0x{hrBorder:X8} corner=0x{hrCorner:X8}");
        return (hrBorder, hrCorner);
    }

    public static void Strip(IntPtr hwnd)
    {
        var style = Style(hwnd);
        if (Chrome.HasCaption(style)) SetStyle(hwnd, Chrome.StrippedStyle(style));
        SetDwmFrameless(hwnd, true);
    }

    public static void Unstrip(IntPtr hwnd, uint originalStyle)
    {
        SetStyle(hwnd, Style(hwnd) | (originalStyle & Chrome.WS_CAPTION));
        SetDwmFrameless(hwnd, false);
    }

    public static Rect ClientOnScreen(IntPtr hwnd)
    {
        GetClientRect(hwnd, out var c);
        var tl = new POINT();
        ClientToScreen(hwnd, ref tl);
        return new Rect(tl.X, tl.Y, tl.X + c.Width, tl.Y + c.Height);
    }

    public static Rect MonitorRect(IntPtr hwnd)
    {
        var mi = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), ref mi);
        return mi.rcMonitor;
    }

    public static void Move(IntPtr hwnd, Rect r) =>
        SetWindowPos(hwnd, IntPtr.Zero, r.Left, r.Top, r.Width, r.Height, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_ASYNCWINDOWPOS);

    // SWP_NOSENDCHANGING skips the app's WM_WINDOWPOSCHANGING, which is where both the max-track-size clamp and
    // Chromium's "a maximized window stays at its maximized rect" enforcement happen. Needed to stretch the window
    // above its slot; measured in the add-chrome-crop spike.
    public static void MoveUnclamped(IntPtr hwnd, Rect r)
    {
        if (!SetWindowPos(hwnd, IntPtr.Zero, r.Left, r.Top, r.Width, r.Height,
                SWP_NOSENDCHANGING | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_ASYNCWINDOWPOS))
            Log.Win32($"SetWindowPos(unclamped) 0x{hwnd:X}");
    }

    // The Windows 11 system backdrop (Mica etc.) is painted over the whole window rect and ignores the window
    // region, so a cropped window's hidden strip shows as a #202020 band on a monitor above (measured on VS Code:
    // 63 grey rows with backdrop type 2, 0 with DWMSBT_NONE). Cropped windows get no backdrop; restore afterwards.
    public static uint? GetBackdrop(IntPtr hwnd) =>
        DwmGetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, out var v, sizeof(uint)) == 0 ? v : null;

    public static void SetBackdrop(IntPtr hwnd, uint type)
    {
        var hr = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref type, sizeof(uint));
        if (hr != 0) Log.Info($"DwmSetWindowAttribute(backdrop={type}) 0x{hwnd:X} hr=0x{hr:X8}");
    }

    // DWM also draws the frame (#2B2B2B in dark mode) outside the region: with the backdrop off, a 9px line was
    // left at the top of each cropped window (measured; 0 after disabling). Off while cropped, default afterwards.
    public static void SetFrameRendering(IntPtr hwnd, bool on)
    {
        var policy = on ? DWMNCRP_USEWINDOWSTYLE : DWMNCRP_DISABLED;
        var hr = DwmSetWindowAttribute(hwnd, DWMWA_NCRENDERING_POLICY, ref policy, sizeof(uint));
        if (hr != 0) Log.Info($"DwmSetWindowAttribute(ncrendering={policy}) 0x{hwnd:X} hr=0x{hr:X8}");
    }

    public static bool IsFrameRendered(IntPtr hwnd) =>
        DwmGetWindowAttribute(hwnd, DWMWA_NCRENDERING_ENABLED, out var v, sizeof(uint)) == 0 && v != 0;

    // Region in window coordinates; null removes it. The system owns the region once SetWindowRgn succeeds.
    public static void SetRegion(IntPtr hwnd, Rect? region)
    {
        var rgn = region is { } r ? CreateRectRgn(r.Left, r.Top, r.Right, r.Bottom) : IntPtr.Zero;
        if (SetWindowRgn(hwnd, rgn, true) == 0)
        {
            Log.Info($"SetWindowRgn failed 0x{hwnd:X} region={region}");
            if (rgn != IntPtr.Zero) DeleteObject(rgn);
        }
    }

    public static string ClassName(IntPtr hwnd)
    {
        var buf = new char[256];
        return new string(buf, 0, GetClassName(hwnd, buf, buf.Length));
    }

    public static string Title(IntPtr hwnd)
    {
        var buf = new char[256];
        return new string(buf, 0, GetWindowText(hwnd, buf, buf.Length));
    }
}
