using System.Diagnostics;
using System.Runtime.InteropServices;
using static WindowManager.Native;

namespace WindowManager;

// The only place the manager touches the parts of Windows that exist for the INPUT desktop alone: injected input,
// the foreground window and the cursor. Normally every method forwards straight to Windows.
//
// In sandbox mode (--sandbox) the manager runs on a hidden desktop next to the user's, where (measured) SendInput
// delivers nothing, the foreground window is always null and a stray call could never be allowed to reach the
// user's own windows. There the same calls go to a virtual foreground, a virtual cursor and a recorded emission log
// instead: what the manager WOULD have typed or clicked is captured exactly (and posted to the target window as
// ordinary messages), so a macro can assert on it. Nothing here is ever sent to the real input stream.
static class Desk
{
    public static bool Sandbox { get; private set; }
    public static void EnterSandbox() => Sandbox = true;

    // One thing the manager emitted, with a system-wide timestamp (QPC ticks) comparable to other processes' clocks.
    public readonly record struct Emit(long Ticks, string Kind, int Vk, bool Up, int Data, long Target);

    static readonly List<Emit> emitted = [];
    static readonly object gate = new();
    public static int EmitCount { get { lock (gate) return emitted.Count; } }
    public static Emit[] EmittedSince(int index) { lock (gate) return [.. emitted.Skip(index)]; }
    static void Record(string kind, int vk = 0, bool up = false, int data = 0, long target = 0)
    {
        lock (gate) emitted.Add(new Emit(Stopwatch.GetTimestamp(), kind, vk, up, data, target));
    }

    // ---- foreground ----

    static nint vfg;
    static POINT vcursor;

    // Raised (on the calling thread) after the virtual foreground changed; Program turns it into the foreground WinEvent
    // Windows would have raised, so the manager's activation logic runs exactly as it does on the real desktop.
    public static Action<IntPtr>? ForegroundChanged;

    // The foreground window as Windows reports it. In the sandbox it follows what Windows would do when the foreground window
    // closes or is minimized: the next visible window down the z-order takes over (or nothing is foreground).
    public static IntPtr Foreground()
    {
        if (!Sandbox) return GetForegroundWindow();
        var h = Volatile.Read(ref vfg);
        if (h != 0 && (!IsWindow(h) || IsIconic(h)))
        {
            var next = NextForegroundAfterLoss();
            if (Interlocked.CompareExchange(ref vfg, next, h) == h)
            {
                if (next != IntPtr.Zero) { Record("foreground", target: next); ForegroundChanged?.Invoke(next); }
                h = next;
            }
            else h = Volatile.Read(ref vfg);
        }
        return h;
    }

    // Sandbox only: nothing is focused (a test starting point; nothing raises an event).
    public static void ClearForeground() { if (Sandbox) Interlocked.Exchange(ref vfg, 0); }

    // For the sampler: the stored value, with no side effects.
    public static IntPtr RawForeground() => Volatile.Read(ref vfg);

    // A physical key that reached the application unchanged (the manager did not swallow it).
    public static void RecordPass(int vk, bool up) => Record("pass", vk, up);

    // A physical key the manager took for itself (or masked): the app never saw it as typed.
    public static void RecordSwallow(int vk) => Record("swallow", vk);

    // ---- virtual desktops ----

    static readonly HashSet<nint> away = [];   // sandbox: windows standing in for "on another virtual desktop"

    public static bool OnCurrentDesktop(IntPtr hwnd)
    {
        if (!Sandbox) return VirtualDesktops.IsOnCurrent(hwnd);
        lock (away) return !away.Contains(hwnd);
    }

    // Sandbox only: the user moved to another desktop, leaving this window behind (or came back).
    public static void MarkAway(IntPtr hwnd, bool isAway)
    {
        lock (away) { if (isAway) away.Add(hwnd); else away.Remove(hwnd); }
        if (isAway && Volatile.Read(ref vfg) == hwnd) Interlocked.Exchange(ref vfg, 0);   // nothing on the new desktop is focused
    }

    public static bool SetForeground(IntPtr hwnd)
    {
        // Focusing a window on another virtual desktop makes Windows switch to that desktop: never on the user's behalf.
        if (hwnd != IntPtr.Zero && !OnCurrentDesktop(hwnd)) { Log.Info($"not focusing 0x{hwnd:X}: it is on another virtual desktop"); return false; }
        if (!Sandbox) return SetForegroundWindow(hwnd);
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return false;
        var prev = Interlocked.Exchange(ref vfg, hwnd);
        // Activating raises a window to the top of the z-order, like the real thing.
        // Topmost then not-topmost lands it above every ordinary window. A plain HWND_TOP stops below the desktop's active window when the caller
        // cannot activate, and on a hidden desktop nobody can. Async: never wait for the target's thread; the two calls stay in order.
        const uint raise = SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS;
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, raise);
        SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, raise);
        if (prev != hwnd)
        {
            Record("foreground", target: hwnd);
            ForegroundChanged?.Invoke(hwnd);
        }
        return true;
    }

    // What Windows does when the foreground window goes away: the next visible window down the z-order takes over.
    public static IntPtr NextForegroundAfterLoss()
    {
        IntPtr pick = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || IsIconic(h) || (Frames.ExStyle(h) & Chrome.WS_EX_TOOLWINDOW) != 0 || Frames.Title(h).Length == 0) return true; // a real application window has a title
            GetWindowThreadProcessId(h, out var pid);
            if (pid == Environment.ProcessId) return true;
            pick = h; return false;
        }, IntPtr.Zero);
        return pick;
    }

    // ---- cursor ----

    public static void GetCursor(out POINT p)
    {
        if (!Sandbox) { GetCursorPos(out p); return; }
        lock (gate) p = vcursor;
    }

    public static bool SetCursor(int x, int y)
    {
        if (!Sandbox) return SetCursorPos(x, y);
        lock (gate) vcursor = new POINT { X = x, Y = y };
        Record("cursor", data: (x & 0xFFFF) | (y << 16));
        return true;
    }

    // ---- keyboard ----

    // Posted to the foreground window as ordinary key messages (a best-effort delivery for fixtures that count them);
    // the recorded stroke is what the assertions use. Modifiers are tracked here so system keys get WM_SYS* messages.
    public static void SendKeys(KeyStroke[] strokes)
    {
        if (strokes.Length == 0) return;
        if (!Sandbox)
        {
            var inputs = strokes.Select(s => new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = (ushort)s.Vk,
                        dwFlags = (s.Up ? KEYEVENTF_KEYUP : 0) | (Vk.IsExtended(s.Vk) ? KEYEVENTF_EXTENDEDKEY : 0),
                    },
                },
            }).ToArray();
            if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) != inputs.Length) Log.Win32("SendInput(keys)");
            return;
        }
        var target = Foreground();
        foreach (var s in strokes)
        {
            Record("key", s.Vk, s.Up, target: target);
            PostKey(target, s.Vk, s.Up);
        }
    }

    static void PostKey(IntPtr hwnd, int vk, bool up)
    {
        if (hwnd == IntPtr.Zero || vk == Vk.Mask) return;
        var scan = MapVirtualKey((uint)vk, 0);
        var lParam = (nint)(1 | (scan << 16) | (up ? 0xC0000000 : 0));
        PostMessage(hwnd, up ? (uint)WM_KEYUP : WM_KEYDOWN, vk, lParam);
    }

    // Is the window that has the focus the target (or its owner / owned popup)? F11 typed at anything else toggles the WRONG window.
    public static bool HasFocus(IntPtr target)
    {
        var fg = Foreground();
        return fg == target || (fg != IntPtr.Zero && (GetAncestor(fg, GA_ROOTOWNER) == target || GetAncestor(target, GA_ROOTOWNER) == fg));
    }

    // F11 for a specific window. It is typed only if that window is on the desktop the user is looking at AND has the focus: F11 goes
    // to the focused window, so anything else toggles a stranger's fullscreen (the user having moved on mid-operation). False = not sent.
    public static bool SendF11(IntPtr target)
    {
        if (!OnCurrentDesktop(target)) { Log.Info($"F11 withheld from 0x{target:X}: it is on another virtual desktop"); return false; }
        if (!HasFocus(target)) { Log.Info($"F11 withheld from 0x{target:X}: the focus is on another window"); return false; }
        if (!Sandbox)
        {
            INPUT[] keys =
            [
                new() { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = 0x7A } } },
                new() { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = 0x7A, dwFlags = KEYEVENTF_KEYUP } } },
            ];
            if (SendInput((uint)keys.Length, keys, Marshal.SizeOf<INPUT>()) != keys.Length) Log.Win32("SendInput(F11)");
            return true;
        }
        Record("f11", 0x7A, target: target);
        PostKey(target, 0x7A, false);
        Thread.Sleep(15);
        PostKey(target, 0x7A, true);
        return true;
    }

    // For a window that cannot be focused without dragging the user to its desktop (shutdown): the key goes to the window itself,
    // as key messages. Chromium honours them (measured on a hidden desktop); an app that ignores them simply stays as it is.
    public static void PostF11(IntPtr target)
    {
        if (Sandbox) Record("f11", 0x7A, target: target);
        PostKey(target, 0x7A, false);
        Thread.Sleep(15);
        PostKey(target, 0x7A, true);
    }

    // ---- mouse ----

    public static void SendMouse(params INPUT[] inputs)
    {
        if (inputs.Length == 0) return;
        if (!Sandbox)
        {
            if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) != inputs.Length) Log.Win32("SendInput(mouse)");
            return;
        }
        foreach (var i in inputs)
        {
            var f = i.u.mi.dwFlags;
            if ((f & MOUSEEVENTF_WHEEL) != 0) Record("wheel", data: i.u.mi.mouseData, target: Foreground());
            else if ((f & MOUSEEVENTF_HWHEEL) != 0) Record("hwheel", data: i.u.mi.mouseData, target: Foreground());
            else if ((f & MOUSEEVENTF_LEFTDOWN) != 0) { Record("click-down", target: Foreground()); PostClick(true); }
            else if ((f & MOUSEEVENTF_LEFTUP) != 0) { Record("click-up", target: Foreground()); PostClick(false); }
        }
    }

    // A click lands on whatever window is under the virtual cursor, in its client coordinates.
    static void PostClick(bool down)
    {
        POINT p; lock (gate) p = vcursor;
        var h = WindowFromPoint(p);
        if (h == IntPtr.Zero) return;
        var c = p; ScreenToClient(h, ref c);
        PostMessage(h, down ? WM_LBUTTONDOWN : WM_LBUTTONUP, down ? 1 : 0, (nint)((c.X & 0xFFFF) | (c.Y << 16)));
    }

    public const uint WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;
    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr hwnd, ref POINT p);
}
