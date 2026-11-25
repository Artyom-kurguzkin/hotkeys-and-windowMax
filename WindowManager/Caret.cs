using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace WindowManager;

public static class Caret
{
    // Where the text caret is on screen, in the foreground window: the classic caret (edit controls, most Win32 editors) first, then
    // UI Automation's text selection (Chromium, WPF, Word and the like have no classic caret). Null when neither knows.
    public static Rectangle? Rect()
    {
        if (ClassicCaret() is { } classic) return classic;
        try
        {
            // UI Automation from a thread pool (MTA) thread, with a deadline: a hung target app must not hang the hotkey.
            var task = Task.Run(AutomationCaret);
            return task.Wait(500) ? task.Result : null;
        }
        catch { return null; }
    }

    static Rectangle? ClassicCaret()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return null;
        var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        if (!GetGUIThreadInfo(GetWindowThreadProcessId(fg, out _), ref info) || info.hwndCaret == IntPtr.Zero) return null;
        var c = info.rcCaret;
        if (c.bottom <= c.top) return null;
        var topLeft = new PT { x = c.left, y = c.top };
        var bottomRight = new PT { x = c.right, y = c.bottom };
        if (!ClientToScreen(info.hwndCaret, ref topLeft) || !ClientToScreen(info.hwndCaret, ref bottomRight)) return null;
        return Rectangle.FromLTRB(topLeft.x, topLeft.y, Math.Max(bottomRight.x, topLeft.x + 1), bottomRight.y);
    }

    static Rectangle? AutomationCaret()
    {
        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused is null || !focused.TryGetCurrentPattern(TextPattern.Pattern, out var pattern)) return null;
            var selection = ((TextPattern)pattern).GetSelection();
            if (selection.Length == 0) return null;
            if (FirstRect(selection[0]) is { } direct) return direct;
            // An empty range (a bare caret) often reports no rectangle: stretch a copy over the next character and use that one's left edge.
            var clone = selection[0].Clone();
            clone.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, 1);
            if (FirstRect(clone) is { } next) return new Rectangle(next.Left, next.Top, 1, next.Height);
            // At the end of the text there is no next character: the previous one's right edge is where the caret is.
            var back = selection[0].Clone();
            back.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -1);
            if (FirstRect(back) is { } prev) return new Rectangle(prev.Right, prev.Top, 1, prev.Height);
        }
        catch { }
        return null;
    }

    static Rectangle? FirstRect(TextPatternRange range)
    {
        foreach (var r in range.GetBoundingRectangles())
            if (!r.IsEmpty && r.Height > 0 && !double.IsInfinity(r.X) && !double.IsInfinity(r.Y))
                return Rectangle.Round(new RectangleF((float)r.X, (float)r.Y, (float)r.Width, (float)r.Height));
        return null;
    }


    [StructLayout(LayoutKind.Sequential)] struct PT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] struct RC { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)]
    struct GUITHREADINFO
    {
        public int cbSize, flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public RC rcCaret;
    }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO info);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hWnd, ref PT pt);
}
