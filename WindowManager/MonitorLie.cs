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

    // A non-maximized window's visible frame can overhang the monitor (Chromium parks itself at (-13,-10) when it is
    // "as big as the screen" without being zoomed). A slot must be on-screen, or the app fills something we can't see
    // and the invariant check screams MISMATCH forever.
    public static Rect Clamp(Rect r, Rect bounds) => new(
        Math.Max(r.Left, bounds.Left), Math.Max(r.Top, bounds.Top), Math.Min(r.Right, bounds.Right), Math.Min(r.Bottom, bounds.Bottom));

    public enum Probe { Capable, LieMissed, NotCapable, Landed }

    // What one F11 probe proved. after≈slot: the lie worked. after≈monitor: the app DID go fullscreen (so it is
    // capable) but into the REAL monitor - the lie wasn't live in that process yet - retry, never mark not-capable
    // (that false negative sidelined Edge for a whole session). after≈before: F11 did nothing - not capable.
    // Anything else: it reacted but landed oddly; treat as a miss and let the retry cap decide.
    public static Probe ProbeVerdict(Rect before, Rect after, Rect slot, Rect monitor) =>
        FilledSlot(after, slot) ? Probe.Capable
        : FilledSlot(after, monitor) ? Probe.LieMissed
        : FilledSlot(after, before) ? Probe.NotCapable
        : Probe.Landed;   // it reacted but ended neither on the slot nor the monitor (e.g. the app cannot be that small): one retry, never a verdict on the program

    // The app is in its OWN fullscreen right now: its rect is (within 2px) exactly the area it believes is its
    // monitor - Chromium stops 1px short at the bottom for an auto-hide taskbar. A maximized window never matches:
    // it overhangs by its 8-12px frame. F11 is a TOGGLE, so this must be known before sending it: assuming
    // "F11 = enter" flipped an already-fullscreen Edge OUT on every probe and cascaded into "not-capable".
    public static bool IsAppFullscreen(Rect rect, Rect area, int tol = 2) =>
        Math.Abs(rect.Left - area.Left) <= tol && Math.Abs(rect.Top - area.Top) <= tol &&
        Math.Abs(rect.Right - area.Right) <= tol && Math.Abs(rect.Bottom - area.Bottom) <= tol;

    // A fullscreen window was moved to another monitor by the user (Win+Shift+Arrow, a drag): where its slot goes. The whole old
    // monitor becomes the whole new one; a tile keeps its proportions. Before this the slot stayed on the OLD monitor and the
    // app's next re-read (any Alt+Tab) snapped the window back there (user report, 2026-10-31).
    public static Rect FollowMonitor(Rect slot, Rect from, Rect to) =>
        IsAppFullscreen(slot, from, 2) ? to : Monitors.Map(slot, from, to);

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
public static class MonitorLie
{
    // Shared layout, kept in step with monlie.c: a table of Slots entries of EntrySize bytes each -
    // { long monitor; int enabled; int reserved; RECT slot }.
    public const int Slots = 8, EntrySize = 32, ShmSize = Slots * EntrySize;
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
        foreach (var pid in procs.Keys.ToArray()) DisableAll(pid);
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

    static IntPtr Entry(IntPtr view, int i) => view + i * EntrySize;

    // The entry for this monitor, or a free one. -1 = the table is full (eight monitors lied at once in one process).
    static int Find(IntPtr view, IntPtr monitor, bool allocate)
    {
        var free = -1;
        for (var i = 0; i < Slots; i++)
        {
            var e = Entry(view, i);
            if (Marshal.ReadInt64(e) == (long)monitor) return i;
            if (free < 0 && Marshal.ReadInt32(e, 8) == 0) free = i;
        }
        return allocate ? free : -1;
    }

    // Lie about ONE monitor of the process: write the slot, then the handle, then flip enabled, so the DLL never reads a
    // half-written entry. Only windows on that monitor are affected; the process's other monitors stay truthful.
    public static void SetSlot(uint pid, IntPtr monitor, Rect slot)
    {
        var view = View(pid);
        if (view == IntPtr.Zero) return;
        var i = Find(view, monitor, allocate: true);
        if (i < 0) { Log.Info($"monitor-lie: pid {pid} already lies about {Slots} monitors, cannot add 0x{monitor:X}"); return; }
        var e = Entry(view, i);
        Marshal.WriteInt32(e, 8, 0);
        Marshal.WriteInt32(e, 16, slot.Left);
        Marshal.WriteInt32(e, 20, slot.Top);
        Marshal.WriteInt32(e, 24, slot.Right);
        Marshal.WriteInt32(e, 28, slot.Bottom);
        Marshal.WriteInt64(e, 0, (long)monitor);
        Marshal.WriteInt32(e, 8, 1);
    }

    public static void Disable(uint pid, IntPtr monitor)
    {
        if (!procs.TryGetValue(pid, out var p)) return;
        var i = Find(p.View, monitor, allocate: false);
        if (i >= 0) Marshal.WriteInt32(Entry(p.View, i), 8, 0);
    }

    public static void DisableAll(uint pid)
    {
        if (!procs.TryGetValue(pid, out var p)) return;
        for (var i = 0; i < Slots; i++) Marshal.WriteInt32(Entry(p.View, i), 8, 0);
    }

    // What the process is being told right now (for the state dump and the layout test).
    public static (IntPtr Monitor, bool Enabled, Rect Slot)[] Entries(uint pid)
    {
        if (!procs.TryGetValue(pid, out var p)) return [];
        var list = new List<(IntPtr, bool, Rect)>();
        for (var i = 0; i < Slots; i++)
        {
            var e = Entry(p.View, i);
            var mon = (IntPtr)Marshal.ReadInt64(e);
            if (mon == IntPtr.Zero) continue;
            list.Add((mon, Marshal.ReadInt32(e, 8) != 0, new Rect(Marshal.ReadInt32(e, 16), Marshal.ReadInt32(e, 20), Marshal.ReadInt32(e, 24), Marshal.ReadInt32(e, 28))));
        }
        return [.. list];
    }

    // Create the process's mapping (all entries disabled) as soon as a window is managed, so by the time we probe the
    // injected DLL has long since opened it. Creating it only in SetSlot, ~400ms before F11, lost that race.
    public static void Touch(uint pid)
    {
        if (procs.ContainsKey(pid)) return;
        View(pid);
    }

    static void ReleaseAll()
    {
        foreach (var (_, p) in procs) { UnmapViewOfFile(p.View); CloseHandle(p.Map); }
        procs.Clear();
    }

    // Poll (up to timeoutMs) until the window's rect satisfies pred. Every F11 transition waits on this instead of a
    // fixed sleep: a Sleep(350) raced the app's own exit animation, and the enter F11 that followed was sometimes
    // swallowed mid-transition (the same tile passed one run and failed the next).
    public static bool WaitRect(IntPtr hwnd, Func<Rect, bool> pred, int timeoutMs = 1500)
    {
        var end = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < end)
        {
            if (GetWindowRect(hwnd, out var r) && pred(r)) return true;
            Thread.Sleep(10);   // was 50: the op finishes when the window has, not up to 50ms later
        }
        return false;
    }

    // Make hwnd the focused window as quickly as possible, so F11 reaches it. Replaces a fixed 80ms sleep: usually the window already
    // IS focused (the user just activated it) and this returns at once. False when the user has moved to another virtual desktop (never
    // focus a window there: Windows would switch to it) or the focus would not move; then no F11 is typed.
    public static bool Focus(IntPtr hwnd, int maxMs = 150)
    {
        if (!Desk.OnCurrentDesktop(hwnd)) return false;
        if (Desk.HasFocus(hwnd)) return true;
        Desk.SetForeground(hwnd);
        var end = Environment.TickCount64 + maxMs;
        while (Environment.TickCount64 < end)
        {
            if (!Desk.OnCurrentDesktop(hwnd)) return false;
            if (Desk.HasFocus(hwnd)) { Thread.Sleep(20); return true; }   // a moment for the app to take the activation before the key arrives
            Thread.Sleep(4);
        }
        return Desk.HasFocus(hwnd);
    }

    // Make the app leave its own fullscreen (wherever it is: its old slot or the real monitor) and wait until it has.
    public static bool LeaveFullscreen(IntPtr hwnd, Rect oldSlot, Rect monitor)
    {
        if (!Focus(hwnd) || !Desk.SendF11(hwnd)) return false;
        return WaitRect(hwnd, r => !LieLogic.IsAppFullscreen(r, oldSlot) && !LieLogic.IsAppFullscreen(r, monitor));
    }

    // Make the app enter its own fullscreen into slot (the lie must already be set) and wait until it fills it.
    // One retry, but only if it is observably not fullscreen anywhere: if it went fullscreen to the real monitor
    // instead, the lie wasn't live and a second F11 would just toggle it back OUT (the verdict handles that case).
    public static bool EnterFullscreen(IntPtr hwnd, Rect slot, Rect monitor)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (!Focus(hwnd) || !Desk.SendF11(hwnd)) return false;   // nothing typed unless this window is focused on this desktop
            if (WaitRect(hwnd, r => LieLogic.FilledSlot(r, slot))) return true;
            if (GetWindowRect(hwnd, out var r) && LieLogic.IsAppFullscreen(r, monitor)) return false; // fullscreen, wrong place
        }
        return false;
    }

    // Probe: enter fullscreen and return where it ended up (null if the window vanished); the caller judges it
    // with LieLogic.ProbeVerdict. The slot must already be set + enabled. Steals focus briefly.
    public static Rect? TryFullscreen(IntPtr hwnd, Rect slot, Rect monitor)
    {
        EnterFullscreen(hwnd, slot, monitor);
        return GetWindowRect(hwnd, out var r) ? r : null;
    }

}
