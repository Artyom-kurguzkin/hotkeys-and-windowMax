using System.Runtime.InteropServices;
using static WindowManager.Native;

namespace WindowManager;

// Pure decision: did the app's own fullscreen fill the slot we lied about? A real fullscreen
// lands within a few pixels of the slot; anything else means the app ignored the trigger.
public static class LieLogic
{
    public static bool FilledSlot(Rect window, Rect slot, int tol = 8) =>
        Math.Abs(window.Left - slot.Left) <= tol && Math.Abs(window.Top - slot.Top) <= tol &&
        Math.Abs(window.Right - slot.Right) <= tol && Math.Abs(window.Bottom - slot.Bottom) <= tol;

    // The slot a window should believe is its whole monitor: the exact rect Windows laid it out at.
    // Maximized -> the monitor rect (so fullscreen covers the monitor, taskbar and all); snapped or
    // floating -> the window rect Windows gave it. All in physical pixels (the manager is PMv2).
    public static Rect SlotFor(Rect window, Rect monitor, bool zoomed) => zoomed ? monitor : window;

    // Win+Left / Win+Right tiling regions within an area (monitor bounds for a fullscreen window, work area for a
    // normal one). Integer-split so left+right exactly cover the area with no seam.
    public static Rect LeftHalf(Rect a) => a with { Right = a.Left + a.Width / 2 };
    public static Rect RightHalf(Rect a) => a with { Left = a.Left + a.Width / 2 };

    // General rule for placing ANY window into a target region: a window's visible edges (its DWM extended frame)
    // sit inside its window rect by the invisible resize borders. To make what the user SEES land exactly on the
    // target - so nothing ever leaves a wallpaper gap against a neighbour - push the window rect out by those insets.
    // window = GetWindowRect, frame = DWM extended frame bounds (the visible rectangle).
    public static Rect AlignVisible(Rect window, Rect frame, Rect target) => new(
        target.Left - (frame.Left - window.Left),
        target.Top - (frame.Top - window.Top),
        target.Right + (window.Right - frame.Right),
        target.Bottom + (window.Bottom - frame.Bottom));
}

// Win32 side of the monitor lie: installs the system-wide hook that maps monlie.dll into every
// GUI process, and gives each managed app process a shared { enabled, slot } the DLL reads so
// that app's own fullscreen sizes itself to the slot.
static class MonitorLie
{
    const int ShmSize = 32;              // int enabled + RECT{l,t,r,b}, rounded up
    const int VK_F11 = 0x7A;
    static IntPtr hook, lib;
    static readonly Dictionary<uint, (IntPtr Map, IntPtr View)> procs = [];

    public static string DllPath => Path.Combine(AppContext.BaseDirectory, "monlie.dll");

    public static bool Install()
    {
        if (!File.Exists(DllPath)) { Log.Info($"monitor-lie: {DllPath} missing, lie disabled"); return false; }
        lib = LoadLibrary(DllPath);
        if (lib == IntPtr.Zero) { Log.Win32("LoadLibrary(monlie.dll)"); return false; }
        var proc = GetProcAddress(lib, "GetMsgProc");
        if (proc == IntPtr.Zero) { Log.Win32("GetProcAddress(GetMsgProc)"); return false; }
        hook = SetWindowsHookExPtr(WH_GETMESSAGE, proc, lib, 0);
        if (hook == IntPtr.Zero) { Log.Win32("SetWindowsHookEx(WH_GETMESSAGE)"); return false; }
        Log.Info("monitor-lie hook installed");
        return true;
    }

    public static void Uninstall()
    {
        foreach (var pid in procs.Keys.ToArray()) Disable(pid);
        ReleaseAll();
        if (hook != IntPtr.Zero) { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; Log.Info("monitor-lie hook removed"); }
    }

    static IntPtr View(uint pid)
    {
        if (procs.TryGetValue(pid, out var p)) return p.View;
        var map = CreateFileMapping(INVALID_HANDLE_VALUE, IntPtr.Zero, PAGE_READWRITE, 0, ShmSize, $"Local\\monlie_{pid}");
        if (map == IntPtr.Zero) { Log.Win32($"CreateFileMapping(pid={pid})"); return IntPtr.Zero; }
        var view = MapViewOfFile(map, FILE_MAP_WRITE, 0, 0, (UIntPtr)ShmSize);
        if (view == IntPtr.Zero) { Log.Win32($"MapViewOfFile(pid={pid})"); CloseHandle(map); return IntPtr.Zero; }
        procs[pid] = (map, view);
        return view;
    }

    // Write the slot, then flip enabled, so the DLL never reads a half-written slot.
    public static void SetSlot(uint pid, Rect slot, bool enabled)
    {
        var view = View(pid);
        if (view == IntPtr.Zero) return;
        Marshal.WriteInt32(view, 4, slot.Left);
        Marshal.WriteInt32(view, 8, slot.Top);
        Marshal.WriteInt32(view, 12, slot.Right);
        Marshal.WriteInt32(view, 16, slot.Bottom);
        Marshal.WriteInt32(view, 0, enabled ? 1 : 0);
    }

    public static void Disable(uint pid)
    {
        if (procs.TryGetValue(pid, out var p)) Marshal.WriteInt32(p.View, 0, 0);
    }

    static void ReleaseAll()
    {
        foreach (var (_, p) in procs) { UnmapViewOfFile(p.View); CloseHandle(p.Map); }
        procs.Clear();
    }

    // Trigger the app's own fullscreen and report whether it actually filled the slot. The slot
    // must already be set + enabled. Steals focus briefly (F11 needs the target focused).
    public static bool TryFullscreen(IntPtr hwnd, Rect slot)
    {
        SetForegroundWindow(hwnd);
        Thread.Sleep(350);
        SendF11();
        Thread.Sleep(1200);
        return GetWindowRect(hwnd, out var r) && LieLogic.FilledSlot(r, slot);
    }

    public static void SendF11()
    {
        INPUT[] keys =
        [
            new() { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = VK_F11 } } },
            new() { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = VK_F11, dwFlags = KEYEVENTF_KEYUP } } },
        ];
        if (SendInput((uint)keys.Length, keys, Marshal.SizeOf<INPUT>()) != keys.Length) Log.Win32("SendInput(F11)");
    }
}
