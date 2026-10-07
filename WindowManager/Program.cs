using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using static WindowManager.Native;

namespace WindowManager;

// Win32 side: owns the hook thread and message loop, applies decisions made in Logic.cs, logs everything it does.
static class Program
{
    const string MsgClass = "WindowManagerMsg";
    const int ScrollSteps = 3, ScrollDelayMs = 10;

    static readonly KeyEngine keys = new();

    // All SendInput calls run here, never on the hook thread. SendInput waits while Windows delivers the injected
    // events, and Windows calls our low-level hooks on the hook thread during that wait; a second SendInput from
    // inside that nested hook call never returns (measured: a frozen instance's stack was
    // OnRawInput → SendInput → KeyboardHook → SendInput). One queue keeps injected input in order.
    static readonly BlockingCollection<Action> outbox = [];
    static void Inject(Action send) => outbox.Add(send);
    static HookProc? keyboardProc; // kept in a field so the GC can't collect the delegate native code holds
    static WndProc? wndProc;
    static IntPtr msgWnd;

    const uint HoverIntervalMs = 700, AltSettleMs = 50, WatchdogMs = 30;
    static readonly UIntPtr TimerHover = 1, TimerAltReleased = 2, TimerWatchdog = 3, TimerCropRetry = 4, TimerHighlight = 5;
    const uint CropRetryMs = 300; // Snap Layouts re-applies its position after the animation; retry after that
    static readonly HashSet<IntPtr> cropRetries = [];
    static IntPtr prevActive, lastHover;

    const string TrackPointMatch = "LEN0325"; // substring of the TrackPoint's raw-input device path (Device Manager → hardware IDs)
    static readonly TrackPointScroller scroller = new();
    static IntPtr trackPoint;
    static POINT altDownPos; // cursor when Alt went down; the gesture pins the cursor here
    static HookProc? mouseProc;

    static bool AltHeld => keys.Mods.HasFlag(Mod.Alt);

    // Window changes run off the keyboard hook callback (Windows drops hooks that take too long).
    const uint WM_APP_TOGGLE_CHROME = 0x8001, WM_APP_TOGGLE_REVEAL = 0x8002, WM_APP_MOVED = 0x8004, WM_APP_MOVE_MONITOR = 0x8005, WM_APP_HIGHLIGHT = 0x8006;
    static readonly HashSet<IntPtr> movedWindows = []; // managed windows with a pending location change
    static readonly Dictionary<IntPtr, CropTracker> crops = [];  // self-drawn title bar windows
    static readonly Dictionary<IntPtr, uint> backdrops = [];     // original backdrop of windows we turned it off for
    static readonly Dictionary<string, int> cropOverrides = new(StringComparer.OrdinalIgnoreCase); // tuned DIP per process
    static readonly Dictionary<IntPtr, string> names = [];
    static readonly HashSet<IntPtr> revealed = []; // cropped bar shown again with Win+Alt
    static readonly Dictionary<IntPtr, uint> managed = []; // hwnd → original style
    static readonly HashSet<IntPtr> userShown = [];         // title bar toggled back on with Alt+T
    static readonly Dictionary<IntPtr, (Rect Window, Rect Target)> coverAttempts = [];
    static WinEventProc? winEventProc;

    [STAThread]
    static int Main(string[] args)
    {
        Log.Init(Log.DefaultDir, args.Contains("--verbose"));
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Info($"CRASH {e.ExceptionObject}");
        Log.Info($"start pid={Environment.ProcessId} args=[{string.Join(' ', args)}]");
        // Real pixel rects for every window on every monitor; without it Windows hands us DPI-virtualized coordinates.
        if (!SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)) Log.Win32("SetProcessDpiAwarenessContext");

        LoadCropOverrides();
        if (args.Contains("--dump")) { Dump(); return 0; }
        if (args.Contains("--hotkeys")) { HelpWindow.ShowAndWait(); return 0; } // the list on its own, no hooks

        ReplaceRunningInstances();
        RecoverFromDeadInstance();
        new Thread(() =>
        {
            foreach (var send in outbox.GetConsumingEnumerable())
                try { send(); } catch (Exception e) { Log.Info($"input sender error: {e}"); }
        }) { IsBackground = true, Name = "input-sender" }.Start();
        msgWnd = CreateMessageWindow();
        if (msgWnd == IntPtr.Zero) return 1;

        keyboardProc = KeyboardHook;
        var hook = SetWindowsHookEx(WH_KEYBOARD_LL, keyboardProc, GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero) { Log.Win32("SetWindowsHookEx(WH_KEYBOARD_LL)"); return 1; }

        prevActive = GetForegroundWindow();
        StartTimer(TimerHover, HoverIntervalMs);
        var mouseHook = IntPtr.Zero;
        if (RegisterTrackPoint())
        {
            StartTimer(TimerWatchdog, WatchdogMs);
            mouseProc = MouseHook;
            mouseHook = SetWindowsHookEx(WH_MOUSE_LL, mouseProc, GetModuleHandle(null), 0);
            if (mouseHook == IntPtr.Zero) Log.Win32("SetWindowsHookEx(WH_MOUSE_LL)");
        }

        winEventProc = OnWinEvent;
        var showHook = SetWinEventHook(EVENT_OBJECT_DESTROY, EVENT_OBJECT_SHOW, IntPtr.Zero, winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        var moveHook = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        if (showHook == IntPtr.Zero || moveHook == IntPtr.Zero) Log.Info("SetWinEventHook failed: new windows won't be stripped");
        EnumWindows((hwnd, _) => { Manage(hwnd); return true; }, IntPtr.Zero);

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        UnhookWinEvent(showHook);
        UnhookWinEvent(moveHook);
        UnhookWindowsHookEx(hook);
        if (mouseHook != IntPtr.Zero) UnhookWindowsHookEx(mouseHook);
        RestoreAll();
        State.Delete(State.DefaultPath); // clean exit: nothing left for a successor to undo
        Log.Info("shutdown");
        return 0;
    }

    // #SingleInstance Force for every running instance: ask each to close so it restores its windows; any that
    // doesn't exit in time (frozen, or an old build) is terminated, and RecoverFromDeadInstance undoes its changes.
    static void ReplaceRunningInstances()
    {
        var pids = new HashSet<uint>();
        for (var w = FindWindowEx(HWND_MESSAGE, IntPtr.Zero, MsgClass, null); w != IntPtr.Zero; w = FindWindowEx(HWND_MESSAGE, w, MsgClass, null))
        {
            GetWindowThreadProcessId(w, out var pid);
            if (pid == Environment.ProcessId) continue;
            pids.Add(pid);
            Log.Info($"replacing running instance pid={pid}");
            if (!PostMessage(w, WM_CLOSE, IntPtr.Zero, IntPtr.Zero)) Log.Win32($"PostMessage(WM_CLOSE) pid={pid}");
        }
        foreach (var pid in pids)
        {
            try
            {
                using var p = Process.GetProcessById((int)pid);
                if (p.WaitForExit(3000)) continue;
                Log.Info($"old instance pid={pid} did not exit in 3s: terminating it");
                p.Kill();
                if (!p.WaitForExit(3000)) Log.Info($"old instance pid={pid} still running after Kill");
            }
            catch (ArgumentException) { } // already gone
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                Log.Info($"could not terminate pid={pid}: {e.Message}");
            }
        }
    }

    static void RecoverFromDeadInstance()
    {
        var left = State.Load(State.DefaultPath);
        if (left.Length == 0) return;
        Log.Info($"recovered {State.Recover(left)} windows left modified by a terminated/crashed instance");
        State.Delete(State.DefaultPath);
    }

    static void SaveState() => State.Save(State.DefaultPath,
        managed.Select(kv => new State.Entry(kv.Key, kv.Value,
            crops.TryGetValue(kv.Key, out var t) && t.Slot is { } s ? State.ToArray(s) : null,
            backdrops.TryGetValue(kv.Key, out var b) ? b : null)));

    // Undo the DWM changes a crop needs (see ApplyStep).
    static void BackdropRestore(IntPtr hwnd)
    {
        if (backdrops.Remove(hwnd, out var original)) Frames.SetBackdrop(hwnd, original);
        Frames.SetFrameRendering(hwnd, true);
    }

    static IntPtr CreateMessageWindow()
    {
        wndProc = (hWnd, msg, wParam, lParam) =>
        {
            try
            {
                switch (msg)
                {
                    case WM_DESTROY: PostQuitMessage(0); break;
                    case WM_TIMER: OnTimer((UIntPtr)(ulong)wParam); break;
                    case WM_INPUT: OnRawInput(lParam); break;
                    case WM_APP_TOGGLE_CHROME: ToggleChrome(GetForegroundWindow()); break;
                    case WM_APP_TOGGLE_REVEAL: ToggleReveal(GetForegroundWindow()); break;
                    case WM_APP_MOVED: HandleMovedWindows(); break;
                    case WM_APP_MOVE_MONITOR: MoveToMonitor(GetForegroundWindow(), (int)wParam); break;
                    case WM_APP_HIGHLIGHT: Highlight(GetForegroundWindow()); break;
                }
            }
            catch (Exception e) { Log.Info($"wndproc error msg=0x{msg:X}: {e}"); }
            return DefWindowProc(hWnd, msg, wParam, lParam); // WM_CLOSE → DestroyWindow; WM_INPUT cleanup
        };
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = wndProc,
            hInstance = GetModuleHandle(null), lpszClassName = MsgClass,
        };
        if (RegisterClassEx(ref wc) == 0) { Log.Win32("RegisterClassEx"); return IntPtr.Zero; }
        var hwnd = CreateWindowEx(0, MsgClass, null, 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        if (hwnd == IntPtr.Zero) Log.Win32("CreateWindowEx(message window)");
        return hwnd;
    }

    static IntPtr KeyboardHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        try
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            var down = (int)wParam is WM_KEYDOWN or WM_SYSKEYDOWN;
            var injected = (k.flags & LLKHF_INJECTED) != 0;
            Log.Debug($"key vk=0x{k.vkCode:X2} {(down ? "down" : "up")}{(injected ? " injected" : "")}");

            if (down && !injected && k.vkCode is Vk.LAlt or Vk.RAlt && !AltHeld) GetCursorPos(out altDownPos);
            var d = keys.Feed((int)k.vkCode, down, injected, k.time);
            if (d.MaskFirst)
            {
                var vk = (int)k.vkCode;
                Inject(() => Send(KeyEngine.MaskedRelease(vk))); // the real release is swallowed below, re-sent after the mask
            }
            if (d.Act != Act.None) Run(d.Act, d.Arg);
            if (!down && !injected && k.vkCode is Vk.LAlt or Vk.RAlt) OnAltReleased();
            if (d.Suppress || d.MaskFirst) return 1;
        }
        catch (Exception e) { Log.Info($"keyboard hook error: {e}"); }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    static void Run(Act act, int arg = 0)
    {
        Log.Info($"action {act}");
        var held = keys.Held;
        switch (act)
        {
            case Act.Quit:
                PostMessage(msgWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                break;
            case Act.ToggleChrome:
                PostMessage(msgWnd, WM_APP_TOGGLE_CHROME, IntPtr.Zero, IntPtr.Zero);
                break;
            case Act.ToggleReveal:
                PostMessage(msgWnd, WM_APP_TOGGLE_REVEAL, IntPtr.Zero, IntPtr.Zero);
                break;
            case Act.MoveToMonitor:
                PostMessage(msgWnd, WM_APP_MOVE_MONITOR, arg, IntPtr.Zero);
                break;
            case Act.HighlightWindow:
                PostMessage(msgWnd, WM_APP_HIGHLIGHT, IntPtr.Zero, IntPtr.Zero);
                break;
            case Act.ShowHelp:
                ShowHelp();
                break;
            case Act.Close or Act.ToggleMaximize or Act.Minimize:
                // Posted, like clicking the caption button; a hung app can't block the hook.
                var fg = GetForegroundWindow();
                var cmd = Chrome.WindowCommand(act, IsZoomed(fg));
                Log.Info($"syscommand 0x{cmd:X} -> {Describe(fg)}");
                if (!PostMessage(fg, WM_SYSCOMMAND, (IntPtr)cmd, IntPtr.Zero)) Log.Win32("PostMessage(WM_SYSCOMMAND)");
                break;
            case Act.WheelDown or Act.WheelUp or Act.WheelLeft or Act.WheelRight:
                Inject(() =>
                {
                    Send(KeyEngine.Lift(held));
                    for (var i = 0; i < ScrollSteps; i++)
                    {
                        SendMouse(WheelInput(act));
                        Thread.Sleep(ScrollDelayMs);
                    }
                    Send(KeyEngine.Restore(held, keys.Held));
                });
                break;
            case Act.Click:
                Inject(() =>
                {
                    Send(KeyEngine.Lift(held));
                    SendMouse(MouseInput(MOUSEEVENTF_LEFTDOWN), MouseInput(MOUSEEVENTF_LEFTUP));
                    Send(KeyEngine.Restore(held, keys.Held));
                });
                break;
            default:
                Inject(() => Send([.. KeyEngine.Lift(held), .. KeyEngine.Output(act), .. KeyEngine.Restore(held, keys.Held)]));
                break;
        }
    }

    static void StartTimer(UIntPtr id, uint ms)
    {
        if (SetTimer(msgWnd, id, ms, IntPtr.Zero) == UIntPtr.Zero) Log.Win32($"SetTimer({id})");
    }

    static void OnTimer(UIntPtr id)
    {
        if (id == TimerHover) HoverFocus();
        else if (id == TimerWatchdog) { if (scroller.InGesture && !AltHeld) StopGesture("watchdog"); }
        else if (id == TimerAltReleased) { KillTimer(msgWnd, id); CenterAfterSwitch(); }
        else if (id == TimerHighlight) { KillTimer(msgWnd, id); foreach (var b in borders) ShowWindow(b, SW_HIDE); }
        else if (id == TimerCropRetry)
        {
            KillTimer(msgWnd, id);
            foreach (var hwnd in cropRetries.ToArray())
            {
                cropRetries.Remove(hwnd);
                if (!crops.TryGetValue(hwnd, out var t) || !IsWindow(hwnd)) continue;
                t.Retry();
                ApplyCrop(hwnd);
            }
        }
    }

    // ---- focus ----

    static void OnAltReleased()
    {
        StopGesture("alt released"); // cut scrolling now, don't wait for the next raw-input event
        StartTimer(TimerAltReleased, AltSettleMs); // let Windows settle focus after Alt+Tab
    }

    static void CenterAfterSwitch()
    {
        var now = GetForegroundWindow();
        GetCursorPos(out var pt);
        var inside = GetWindowRect(now, out var r) && r.Contains(pt.X, pt.Y);
        if (Focus.ShouldCenter(prevActive, now, inside) && r.Width > 0 && r.Height > 0)
        {
            var (cx, cy) = r.Center;
            if (SetCursorPos(cx, cy)) Log.Info($"center cursor on {Describe(now)} at {cx},{cy}");
            else Log.Win32("SetCursorPos(center)");
        }
        prevActive = now;
    }

    static void HoverFocus()
    {
        if (AltHeld) return;
        GetCursorPos(out var pt);
        var hwnd = GetAncestor(WindowFromPoint(pt), GA_ROOT);
        var real = Focus.IsRealWindow((uint)GetWindowLong(hwnd, GWL_STYLE));
        if (!Focus.ShouldHoverFocus(hwnd, lastHover, false, real)) return;
        lastHover = hwnd;
        Log.Info($"hover focus {Describe(hwnd)} {(Activate(hwnd) ? "ok" : "REFUSED")}");
    }

    // Like AHK's WinActivate: plain SetForegroundWindow, and if the foreground lock refuses,
    // borrow the foreground thread's input state and retry.
    static bool Activate(IntPtr hwnd)
    {
        if (SetForegroundWindow(hwnd)) return true;
        var fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var me = GetCurrentThreadId();
        AttachThreadInput(me, fgThread, true);
        BringWindowToTop(hwnd);
        var ok = SetForegroundWindow(hwnd);
        AttachThreadInput(me, fgThread, false);
        return ok;
    }

    static string Describe(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        string name;
        try { name = Process.GetProcessById((int)pid).ProcessName; } catch (ArgumentException) { name = "?"; }
        return $"0x{hwnd:X}({name})";
    }

    // ---- borderless windows ----

    static void Manage(IntPtr hwnd)
    {
        if (managed.ContainsKey(hwnd)) return;
        var style = Frames.Style(hwnd);
        var cls = Frames.ClassName(hwnd);
        if (!Chrome.IsManageable(style, Frames.ExStyle(hwnd), cls)) return;
        if (IsIconic(hwnd)) return; // classification needs real geometry; picked up on its next location change

        // Classify before stripping: once WS_CAPTION is gone every window would look self-drawn.
        GetWindowRect(hwnd, out var window);
        var dpi = GetDpiForWindow(hwnd);
        var selfDrawn = Crop.IsSelfDrawn(window, Frames.ClientOnScreen(hwnd), dpi);

        // Register before stripping: SetWindowLong waits on the app, and WinEvents for this window get dispatched
        // re-entrantly during that wait, so they must already see the final classification.
        managed[hwnd] = style;
        if (selfDrawn) crops[hwnd] = new CropTracker();
        SaveState(); // before touching the window, so a successor can undo it even if we die mid-way
        Frames.Strip(hwnd);
        Log.Info($"strip {Describe(hwnd)} class={cls} style=0x{style:X8} -> 0x{Frames.Style(hwnd):X8}" +
                 (selfDrawn ? $" self-drawn crop={Crop.DipFor(ProcessName(hwnd), cropOverrides)}dip@{dpi}" : ""));
        if (selfDrawn) ApplyCrop(hwnd);
        else CoverIfMaximized(hwnd);
    }

    static void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != OBJID_WINDOW || idChild != 0 || hwnd == IntPtr.Zero) return;
        try
        {
            switch (evt)
            {
                case EVENT_OBJECT_SHOW: Manage(hwnd); break;
                case EVENT_OBJECT_DESTROY:
                    userShown.Remove(hwnd); coverAttempts.Remove(hwnd); crops.Remove(hwnd); names.Remove(hwnd); backdrops.Remove(hwnd); revealed.Remove(hwnd);
                    if (managed.Remove(hwnd)) SaveState();
                    break;
                case EVENT_OBJECT_LOCATIONCHANGE when !managed.ContainsKey(hwnd):
                    if (Focus.IsRealWindow(Frames.Style(hwnd)) || Frames.ClassName(hwnd) == Chrome.UwpFrameClass) Manage(hwnd); // e.g. restored from minimized
                    break;
                case EVENT_OBJECT_LOCATIONCHANGE:
                    // Deferred to our own message loop: our window calls (SetWindowRgn, SetWindowLong) dispatch nested
                    // WinEvents, and handling them in place re-entered the crop logic mid-step (measured: impossible
                    // refusal counts, a missed retry). Also merges a snap animation's event burst into one pass.
                    if (movedWindows.Add(hwnd) && movedWindows.Count == 1) PostMessage(msgWnd, WM_APP_MOVED, IntPtr.Zero, IntPtr.Zero);
                    break;
            }
        }
        catch (Exception e) { Log.Info($"win event 0x{evt:X} error: {e}"); }
    }

    static void HandleMovedWindows()
    {
        foreach (var hwnd in movedWindows.ToArray())
        {
            movedWindows.Remove(hwnd);
            if (!managed.ContainsKey(hwnd) || !IsWindow(hwnd)) continue;
            if (Chrome.NeedsRestrip(Frames.Style(hwnd), userShown.Contains(hwnd)))
            {
                Log.Info($"restrip {Describe(hwnd)} (app restored its caption)");
                Frames.Strip(hwnd);
            }
            if (crops.ContainsKey(hwnd)) ApplyCrop(hwnd);
            else CoverIfMaximized(hwnd);
        }
    }

    static void CoverIfMaximized(IntPtr hwnd)
    {
        if (userShown.Contains(hwnd) || !GetWindowRect(hwnd, out var window)) return;
        var monitor = Frames.MonitorRect(hwnd);
        if (Chrome.CoverRect(IsZoomed(hwnd), monitor, window, Frames.ClientOnScreen(hwnd)) is not { } target)
        {
            coverAttempts.Remove(hwnd);
            return;
        }
        if (Chrome.AlreadyTried(coverAttempts.TryGetValue(hwnd, out var last) ? last : null, window, target)) return;
        coverAttempts[hwnd] = (window, target);
        Log.Info($"cover monitor {Describe(hwnd)} {window} -> {target}");
        Frames.Move(hwnd, target);
    }

    // ---- self-drawn title bar crop ----

    static int CropPixels(IntPtr hwnd) => Crop.Pixels(Crop.DipFor(ProcessName(hwnd), cropOverrides), GetDpiForWindow(hwnd));

    static void ApplyCrop(IntPtr hwnd)
    {
        if (revealed.Contains(hwnd) || userShown.Contains(hwnd) || IsIconic(hwnd) || !crops.TryGetValue(hwnd, out var tracker)) return;
        if (!GetWindowRect(hwnd, out var window)) return;
        if (Crop.IsAppFullscreen(window, Frames.MonitorRect(hwnd)))
        {
            if (tracker.Applied is not null)
            {
                tracker.Uncrop(); // the app moved itself to fullscreen: just drop the clip, don't move it
                Frames.SetRegion(hwnd, null);
                BackdropRestore(hwnd);
                SaveState();
                Log.Info($"uncrop {Describe(hwnd)}: app went fullscreen");
            }
            return;
        }
        var client = Frames.ClientOnScreen(hwnd);
        var monitor = Frames.MonitorRect(hwnd);
        // Maximized: the client fills the monitor except Chromium's auto-hide-taskbar sliver at the bottom.
        // Otherwise: fill the rect Windows laid out (frame bounds).
        var visible = IsZoomed(hwnd) ? Crop.MaximizedVisible(client, monitor) : Frames.FrameBounds(hwnd);
        ApplyStep(hwnd, tracker.OnLocation(window, client, CropPixels(hwnd), monitor.Top, visible), window);
    }

    static void ApplyStep(IntPtr hwnd, CropStep step, Rect from)
    {
        // ponytail: rewrites state.json on every crop (a few per window move); debounce if it ever shows up in profiles
        if (step.Kind is CropKind.Apply or CropKind.RegionOnly)
        {
            // The backdrop ignores the window region (see Frames.GetBackdrop): it must be off while cropped.
            if (!backdrops.ContainsKey(hwnd) && Frames.GetBackdrop(hwnd) is { } original) backdrops[hwnd] = original;
            SaveState(); // records the original backdrop before we change it
            Frames.SetBackdrop(hwnd, DWMSBT_NONE); // every crop step: some apps set their backdrop again
            Frames.SetFrameRendering(hwnd, false);  // the DWM frame also ignores the region
        }
        switch (step.Kind)
        {
            case CropKind.Apply:
                Log.Info($"crop {Describe(hwnd)} n={step.Target.Height - from.Height}px {from} -> {step.Target} clip={step.Region}");
                Frames.SetRegion(hwnd, step.Region);
                Frames.MoveUnclamped(hwnd, step.Target);
                break;
            case CropKind.RegionOnly:
                Log.Info($"crop {Describe(hwnd)} back on earlier cropped rect {step.Target}: refresh clip only");
                Frames.SetRegion(hwnd, step.Region);
                break;
            case CropKind.Refused:
                // Seen back on the slot after our crop: Windows re-applying a Snap Layout position after its animation,
                // a stale event from before our async move landed, or an app veto. Look again once things settle:
                // the retry re-crops (still on the slot) or just refreshes the clip (it was stale).
                var refusals = crops.TryGetValue(hwnd, out var t) ? t.Refusals : int.MaxValue;
                if (refusals <= CropTracker.MaxRetries)
                {
                    Log.Info($"crop {Describe(hwnd)} back on its slot {from} after cropping (#{refusals}): re-checking in {CropRetryMs}ms");
                    cropRetries.Add(hwnd);
                    StartTimer(TimerCropRetry, CropRetryMs);
                }
                else
                {
                    // Genuinely refused: a clip made for the stretched rect would cut gaps into the content on the slot.
                    Frames.SetRegion(hwnd, null);
                    Log.Info($"crop {Describe(hwnd)} refused {refusals} times at {from}: clip removed, leaving it uncropped");
                }
                break;
        }
    }

    static void Uncrop(IntPtr hwnd)
    {
        if (!crops.TryGetValue(hwnd, out var tracker)) return;
        if (tracker.Uncrop() is not { } slot) return; // never cropped (e.g. app fullscreen)
        Frames.SetRegion(hwnd, null);
        Frames.MoveUnclamped(hwnd, slot);
        BackdropRestore(hwnd);
        SaveState();
        Log.Info($"uncrop {Describe(hwnd)} -> {slot}");
    }

    // Win+Alt: show or hide the cropped bar of the active window only.
    static void ToggleReveal(IntPtr hwnd)
    {
        if (!crops.ContainsKey(hwnd) || userShown.Contains(hwnd)) { Log.Info($"reveal: {Describe(hwnd)} has no cropped bar"); return; }
        if (revealed.Remove(hwnd))
        {
            Log.Info($"reveal off {Describe(hwnd)}");
            ApplyCrop(hwnd);
        }
        else
        {
            revealed.Add(hwnd);
            Log.Info($"reveal on {Describe(hwnd)}");
            Uncrop(hwnd);
        }
    }

    // Hand-edited only (no hotkey writes it), so a stray key press can never change a good value.
    static string CropFile => Path.Combine(Log.DefaultDir, "crop.json");

    static void LoadCropOverrides()
    {
        try
        {
            if (!File.Exists(CropFile)) return;
            foreach (var (k, v) in JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(CropFile)) ?? [])
                cropOverrides[k] = v;
            Log.Info($"crop overrides: {string.Join(", ", cropOverrides.Select(kv => $"{kv.Key}={kv.Value}"))}");
        }
        catch (Exception e) when (e is IOException or JsonException) { Log.Info($"crop.json unreadable, using defaults: {e.Message}"); }
    }

    // ---- triple Alt: flash a border around the active window ----

    const uint HighlightMs = 1200;
    static readonly IntPtr[] borders = new IntPtr[4]; // top, bottom, left, right strips
    static WndProc? borderProc;
    static IntPtr borderBrush;

    // Our own strips rather than the DWM border: cropped windows have DWM frame drawing off and a clip, so Windows'
    // border can't show on them. Click-through (layered + transparent), topmost, never activated.
    static void Highlight(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var w)) return;
        var area = GetWindowRgnBox(hwnd, out var g) != 0
            ? new Rect(w.Left + g.Left, w.Top + g.Top, w.Left + g.Right, w.Top + g.Bottom)
            : Frames.FrameBounds(hwnd) ?? w;
        var t = Math.Max(2, (int)(4 * GetDpiForWindow(hwnd) / 96)); // 4px at 100%

        // Windows accent colour (what themed borders and highlights use); ARGB -> COLORREF (0x00BBGGRR)
        var color = DwmGetColorizationColor(out var argb, out _) == 0
            ? ((argb >> 16) & 0xFF) | (argb & 0xFF00) | ((argb & 0xFF) << 16)
            : 0x00D77800u;
        if (borderBrush != IntPtr.Zero) DeleteObject(borderBrush);
        borderBrush = CreateSolidBrush(color);

        if (borders[0] == IntPtr.Zero && !CreateBorders()) return;
        Rect[] strips =
        [
            new(area.Left, area.Top, area.Right, area.Top + t),
            new(area.Left, area.Bottom - t, area.Right, area.Bottom),
            new(area.Left, area.Top, area.Left + t, area.Bottom),
            new(area.Right - t, area.Top, area.Right, area.Bottom),
        ];
        for (var i = 0; i < 4; i++)
        {
            SetWindowPos(borders[i], HWND_TOPMOST, strips[i].Left, strips[i].Top, strips[i].Width, strips[i].Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
            InvalidateRect(borders[i], IntPtr.Zero, true);
        }
        StartTimer(TimerHighlight, HighlightMs);
        Log.Info($"highlight {Describe(hwnd)} {area}");
    }

    static bool CreateBorders()
    {
        const string cls = "WindowManagerBorder";
        borderProc = (h, msg, wParam, lParam) =>
        {
            if (msg != WM_ERASEBKGND) return DefWindowProc(h, msg, wParam, lParam);
            GetClientRect(h, out var r);
            FillRect(wParam, ref r, borderBrush);
            return 1;
        };
        var wc = new WNDCLASSEX { cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = borderProc, hInstance = GetModuleHandle(null), lpszClassName = cls };
        if (RegisterClassEx(ref wc) == 0) { Log.Win32("RegisterClassEx(border)"); return false; }
        for (var i = 0; i < 4; i++)
        {
            borders[i] = CreateWindowEx(WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_TOOLWINDOW_EX | WS_EX_NOACTIVATE,
                cls, null, WS_POPUP_STYLE, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
            if (borders[i] == IntPtr.Zero) { Log.Win32("CreateWindowEx(border)"); return false; }
            SetLayeredWindowAttributes(borders[i], 0, 255, LWA_ALPHA); // layered + transparent = click-through
        }
        return true;
    }

    // ---- both Alts: list every hotkey ----

    // Runs on its own UI thread, so it never blocks the hook thread.
    static void ShowHelp() => Log.Info(HelpWindow.Toggle(GetForegroundWindow()) ? "hotkey list opened" : "hotkey list closed");

    // ---- move between monitors (Copilot key + 1-9) ----

    static void MoveToMonitor(IntPtr hwnd, int number)
    {
        var monitors = Frames.AllMonitors();
        var target = monitors.FirstOrDefault(m => Monitors.Number(m.Device) == number);
        var current = Frames.MonitorOf(hwnd);
        if (target is null || current is null)
        {
            Log.Info($"move to monitor {number}: no such monitor (have {string.Join(", ", monitors.Select(m => Monitors.Number(m.Device)))})");
            return;
        }
        if (target.Handle == current.Handle) return;

        var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!GetWindowPlacement(hwnd, ref wp)) { Log.Win32("GetWindowPlacement"); return; }
        var zoomed = IsZoomed(hwnd);
        // rcNormal is in workspace coordinates: relative to the primary monitor's work area.
        var origin = monitors.FirstOrDefault(m => m.Primary)?.Work ?? default;
        Rect ToScreen(Rect r) => new(r.Left + origin.Left, r.Top + origin.Top, r.Right + origin.Left, r.Bottom + origin.Top);
        Rect ToWorkspace(Rect r) => new(r.Left - origin.Left, r.Top - origin.Top, r.Right - origin.Left, r.Bottom - origin.Top);

        // Maximized: keep its restore rect. Otherwise move what is on screen (a snapped window's restore rect is its
        // pre-snap size). Either way, start from Windows' rect, not our stretched crop rect.
        GetWindowRect(hwnd, out var onScreen);
        var rect = zoomed ? ToScreen(wp.rcNormal) : onScreen;
        if (crops.TryGetValue(hwnd, out var tracker) && tracker.SlotFor(rect) is { } slot) rect = slot;

        var mapped = Monitors.Map(rect, current.Work, target.Work);
        wp.rcNormal = ToWorkspace(mapped);
        // Always place it normal on the target first: Windows ignores a new restore rect for a window that is already
        // maximized (measured: it stayed on its monitor). Maximize again once it is there.
        wp.showCmd = SW_SHOWNORMAL;
        wp.flags = 0;
        Frames.SetRegion(hwnd, null); // the old clip is for the old geometry; the crop is redone at the new place
        if (!SetWindowPlacement(hwnd, ref wp)) { Log.Win32("SetWindowPlacement"); return; }
        // Landing on a monitor with another DPI makes the app rescale itself (measured: 1944 wide became 1308);
        // now that it is on the target monitor, set the intended rect again.
        SetWindowPos(hwnd, IntPtr.Zero, mapped.Left, mapped.Top, mapped.Width, mapped.Height, SWP_NOZORDER | SWP_NOACTIVATE);
        if (zoomed) ShowWindow(hwnd, SW_MAXIMIZE);
        Log.Info($"move {Describe(hwnd)} to monitor {number} ({target.Device}){(zoomed ? " maximized" : "")}: {rect} -> {mapped}");
    }

    // ---- shared ----

    static void ToggleChrome(IntPtr hwnd)
    {
        if (!managed.TryGetValue(hwnd, out var original)) { Log.Info($"toggle chrome: {Describe(hwnd)} is not managed"); return; }
        if (userShown.Remove(hwnd))
        {
            Frames.Strip(hwnd);
            Log.Info($"toggle chrome off {Describe(hwnd)}");
            if (crops.TryGetValue(hwnd, out var t)) { t.Uncrop(); ApplyCrop(hwnd); }
            else CoverIfMaximized(hwnd);
        }
        else
        {
            Uncrop(hwnd); // no-op if Win+Alt already revealed it
            revealed.Remove(hwnd);
            userShown.Add(hwnd);
            Frames.Unstrip(hwnd, original);
            Log.Info($"toggle chrome on {Describe(hwnd)}");
        }
    }

    static void RestoreAll()
    {
        foreach (var hwnd in crops.Keys) if (IsWindow(hwnd)) Uncrop(hwnd);
        foreach (var (hwnd, original) in managed)
        {
            if (!IsWindow(hwnd)) continue;
            Frames.Unstrip(hwnd, original);
            if (IsZoomed(hwnd) && !crops.ContainsKey(hwnd)) { ShowWindow(hwnd, SW_RESTORE); ShowWindow(hwnd, SW_MAXIMIZE); } // back onto the work area
        }
        Log.Info($"restored {managed.Count} windows ({crops.Count} uncropped)");
    }

    static string ProcessName(IntPtr hwnd)
    {
        if (names.TryGetValue(hwnd, out var n)) return n;
        GetWindowThreadProcessId(hwnd, out var pid);
        try { n = Process.GetProcessById((int)pid).ProcessName; } catch (ArgumentException) { n = "?"; }
        return names[hwnd] = n;
    }

    static void Dump()
    {
        var lines = new List<string>();
        EnumWindows((hwnd, _) =>
        {
            var style = Frames.Style(hwnd);
            if ((style & Focus.WS_VISIBLE) == 0) return true;
            var ex = Frames.ExStyle(hwnd);
            var cls = Frames.ClassName(hwnd);
            GetWindowRect(hwnd, out var r);
            var dpi = GetDpiForWindow(hwnd);
            var selfDrawn = Crop.IsSelfDrawn(r, Frames.ClientOnScreen(hwnd), dpi);
            lines.Add($"{Describe(hwnd),-32} manageable={Chrome.IsManageable(style, ex, cls),-5} caption={Chrome.HasCaption(style),-5} " +
                      $"selfDrawn={selfDrawn,-5} crop={Crop.DipFor(ProcessName(hwnd), cropOverrides)}dip dpi={dpi} " +
                      $"style=0x{style:X8} ex=0x{ex:X8} rect={r} class={cls} title=\"{Frames.Title(hwnd)}\"");
            return true;
        }, IntPtr.Zero);
        foreach (var l in lines) { Console.WriteLine(l); Log.Info($"dump {l}"); }
    }

    // ---- TrackPoint scroll ----

    static bool RegisterTrackPoint()
    {
        trackPoint = FindTrackPoint();
        if (trackPoint == IntPtr.Zero)
        {
            Log.Info($"TrackPoint scrolling disabled: no raw-input mouse matching '{TrackPointMatch}'");
            return false;
        }
        RAWINPUTDEVICE[] rid = [new() { usUsagePage = 0x01, usUsage = 0x02, dwFlags = RIDEV_INPUTSINK, hwndTarget = msgWnd }];
        if (!RegisterRawInputDevices(rid, 1, Marshal.SizeOf<RAWINPUTDEVICE>())) { Log.Win32("RegisterRawInputDevices"); return false; }
        return true;
    }

    static IntPtr FindTrackPoint()
    {
        uint n = 0;
        var size = Marshal.SizeOf<RAWINPUTDEVICELIST>();
        GetRawInputDeviceList(null, ref n, size);
        var list = new RAWINPUTDEVICELIST[n];
        if (GetRawInputDeviceList(list, ref n, size) == uint.MaxValue) { Log.Win32("GetRawInputDeviceList"); return IntPtr.Zero; }

        var found = IntPtr.Zero;
        foreach (var d in list.Take((int)n).Where(d => d.dwType == RIM_TYPEMOUSE))
        {
            uint len = 0;
            GetRawInputDeviceInfo(d.hDevice, RIDI_DEVICENAME, null, ref len);
            var buf = new char[len];
            GetRawInputDeviceInfo(d.hDevice, RIDI_DEVICENAME, buf, ref len);
            var name = new string(buf).TrimEnd('\0');
            var match = found == IntPtr.Zero && name.Contains(TrackPointMatch, StringComparison.OrdinalIgnoreCase);
            if (match) found = d.hDevice;
            Log.Info($"mouse device 0x{d.hDevice:X} {name}{(match ? "  <- TrackPoint" : "")}");
        }
        return found;
    }

    static void OnRawInput(IntPtr hRaw)
    {
        if (!AltHeld) { StopGesture("alt up"); return; }
        uint size = 0;
        GetRawInputData(hRaw, RID_INPUT, null, ref size, RawMouse.HeaderSize);
        if (size == 0) return;
        var buf = new byte[size];
        if (GetRawInputData(hRaw, RID_INPUT, buf, ref size, RawMouse.HeaderSize) != size) return;
        if (RawMouse.Parse(buf) is not { } m || m.Device != trackPoint || (m.Flags & RawMouse.MouseMoveAbsolute) != 0) return;

        var (x, y, started) = scroller.Motion(m.Dx, m.Dy);
        if (started)
        {
            // From here the mouse hook swallows cursor moves. Only the sample(s) before the gesture was recognised
            // moved the cursor: put it back once to where it was when Alt went down.
            SetCursorPos(altDownPos.X, altDownPos.Y);
            // Lift Alt once for the whole gesture so apps see plain wheel, not Alt+wheel (VS Code fast-scroll etc.).
            // Not re-pressed: the gesture only ends on the physical Alt release, which is masked.
            keys.MarkChordUsed();
            var held = keys.Held;
            Inject(() => Send(KeyEngine.Lift(held)));
            Log.Info($"trackpoint gesture start at {altDownPos.X},{altDownPos.Y}");
        }
        if (x == 0 && y == 0) return;
        // lag = how long WM_INPUT sat in our queue; big lag = our thread was busy, big gaps with no lag = upstream stall
        Log.Debug($"trackpoint dx={m.Dx} dy={m.Dy} -> wheel x={x} y={y} lag={Environment.TickCount - GetMessageTime()}ms");
        INPUT[] wheel = [.. (y != 0 ? [MouseInput(MOUSEEVENTF_WHEEL, -y)] : Array.Empty<INPUT>()),
                         .. (x != 0 ? [MouseInput(MOUSEEVENTF_HWHEEL, x)] : Array.Empty<INPUT>())];
        Inject(() => SendMouse(wheel));
    }

    // Swallows real cursor movement while a TrackPoint gesture runs, so the cursor never moves (no snap-back jitter).
    // Raw input still arrives for swallowed moves (measured), which is what drives the scrolling.
    static IntPtr MouseHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (int)wParam == WM_MOUSEMOVE && scroller.InGesture && AltHeld)
        {
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            if ((info.flags & LLMHF_INJECTED) == 0) return 1;
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    static void StopGesture(string why)
    {
        if (!scroller.InGesture) return;
        scroller.Stop();
        Log.Info($"trackpoint gesture stop ({why})");
    }

    static INPUT WheelInput(Act act) => act switch
    {
        Act.WheelDown => MouseInput(MOUSEEVENTF_WHEEL, -WHEEL_DELTA),
        Act.WheelUp => MouseInput(MOUSEEVENTF_WHEEL, WHEEL_DELTA),
        Act.WheelLeft => MouseInput(MOUSEEVENTF_HWHEEL, -WHEEL_DELTA),
        _ => MouseInput(MOUSEEVENTF_HWHEEL, WHEEL_DELTA),
    };

    static INPUT MouseInput(uint flags, int data = 0) =>
        new() { type = INPUT_MOUSE, u = new InputUnion { mi = new MOUSEINPUT { dwFlags = flags, mouseData = data } } };

    static void SendMouse(params INPUT[] inputs)
    {
        if (inputs.Length == 0) return;
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) != inputs.Length) Log.Win32("SendInput(mouse)");
    }

    static void Send(KeyStroke[] strokes)
    {
        if (strokes.Length == 0) return;
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
    }
}
