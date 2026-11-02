using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using static WindowManager.Native;

namespace WindowManager;

// Win32 side: owns the hook thread and message loop, applies decisions made in Logic.cs, logs everything it does.
static partial class Program
{
    static string MsgClass = "WindowManagerMsg"; // sandbox mode gives it a suffix so it can never match the real manager's window
    const int ScrollSteps = 3, ScrollDelayMs = 10;

    static readonly KeyEngine keys = new();

    // Hint mode (Alt+F, an Act in the key engine so the Alt release is masked like Alt+Q): see HintEngine (Logic.cs) and Hints.cs.
    static readonly HintEngine hints = new();
    static IReadOnlyList<Hints.Target> hintTargets = [];
    static readonly System.Collections.Concurrent.ConcurrentQueue<List<Hints.Target>> hintScans = []; // worker -> loop
    static bool hintScanning, hintCancel; // a scan is on its way / Alt+F was pressed again meanwhile: drop its result

    // All SendInput calls run here, never on the hook thread. SendInput waits while Windows delivers the injected
    // events, and Windows calls our low-level hooks on the hook thread during that wait; a second SendInput from
    // inside that nested hook call never returns (measured: a frozen instance's stack was
    // OnRawInput → SendInput → KeyboardHook → SendInput). One queue keeps injected input in order.
    static readonly BlockingCollection<Action> outbox = [];
    // Lie operations (F11 probes that wait ~1.5s, re-tiles) get their OWN worker. On the input-sender thread they
    // sat in front of every injected keystroke: a desktop switch or scroll pressed during a probe waited ~1s
    // (measured: "action DesktopRight" then a probe, then the switch). SendInput is fine from any non-hook thread.
    static readonly BlockingCollection<(string Name, Stopwatch Queued, Action Run)> opbox = []; // named: a slow one is logged by name
    static void Work(string name, Action run) => opbox.Add((name, Stopwatch.StartNew(), run));
    static void Inject(Action send) => outbox.Add(send);

    // A lie operation (enter / re-tile / reveal / layouts) in flight on the sender thread. While one runs, activation-
    // driven entry is suspended: our own F11 toggles bounce focus through bystander windows for longer than any time
    // debounce, and probing one of them (measured: the Claude app, Word) stole the foreground from the window being
    // operated on, so every later key hit the bystander. WM_APP_OPDONE re-checks the real foreground afterwards.
    static int opsInFlight;
    // Windows with an op in flight, COUNTED: two ops can overlap on one window (a tile and the re-enter it triggers), and the first to
    // finish used to mark the window idle while the second was still running (measured by the sandbox: reconcile and snap logic misjudged it).
    static readonly Dictionary<IntPtr, int> opCounts = [];
    static bool OpActive(IntPtr h) => opCounts.ContainsKey(h);
    static void OpBegin(IntPtr h) => opCounts[h] = opCounts.GetValueOrDefault(h) + 1;
    static void OpEnd(IntPtr h) { if (opCounts.TryGetValue(h, out var n)) { if (n <= 1) opCounts.Remove(h); else opCounts[h] = n - 1; } }
    static readonly Dictionary<IntPtr, int> pendingSnap = []; // latest Win+Arrow requested while one was in flight
    // target: the window the user acted on. When the op completes it is re-asserted as foreground (WM_APP_OPDONE) -
    // an app's fullscreen transition can leave a BYSTANDER foreground (measured: Word), and adopting that as "the
    // user switched" sent every later key to the wrong window. IntPtr.Zero = don't re-assert (Snap Layouts flyout).
    static void Op(IntPtr target, Action body)
    {
        Interlocked.Increment(ref opsInFlight);
        if (target != IntPtr.Zero) OpBegin(target);
        Work($"op on 0x{target:X}", () => // lie-worker, never the input-sender: user keystrokes must not queue behind a transition
        {
            try { body(); }
            finally { Interlocked.Decrement(ref opsInFlight); PostMessage(msgWnd, WM_APP_OPDONE, target, IntPtr.Zero); }
        });
    }
    static HookProc? keyboardProc; // kept in a field so the GC can't collect the delegate native code holds
    static WndProc? wndProc;
    static IntPtr msgWnd;

    const uint HoverIntervalMs = 700, AltSettleMs = 50, WatchdogMs = 30;
    static readonly UIntPtr TimerHover = 1, TimerAltReleased = 2, TimerWatchdog = 3, TimerCropRetry = 4, TimerHighlight = 5,
        TimerSettle = 6, TimerRepair = 7, TimerZone = 8, TimerActivate = 9, TimerPrefetch = 11;

    // Sleep / display changes (see Layout in Logic.cs)
    const uint RepairDelayMs = 500; // after moving windows back, let them land before re-cropping
    static readonly Dictionary<IntPtr, LayoutRecord> layout = []; // last known-good place of each managed window
    static string stableDisplays = "", pendingDisplays = "";
    static bool settling; // displays changing or machine asleep: window moves are not tracked or reacted to
    static long lastTick;
    const int CursorCheckTicks = 5; // every 5th 700ms tick (3.5s): are the hand-drawn cursors still in place?
    static int hoverTicks;
    const uint CropRetryMs = 300; // Snap Layouts re-applies its position after the animation; retry after that
    static readonly HashSet<IntPtr> cropRetries = [];
    static IntPtr prevActive, lastHover;
    static bool altTabbed; // Tab went down while Alt was held: on the Alt release, flash the window the switch landed on
    const uint SwitchHighlightMs = 1500;
    static POINT lastHoverPos; // hover-focus only reacts to a pointer that moved (see Focus.ShouldHoverFocus)

    const string TrackPointMatch = "LEN0325"; // substring of the TrackPoint's raw-input device path (Device Manager → hardware IDs)
    static readonly TrackPointScroller scroller = new();
    static IntPtr trackPoint;
    static POINT altDownPos; // cursor when Alt went down; the gesture pins the cursor here
    static HookProc? mouseProc;

    static bool AltHeld => keys.Mods.HasFlag(Mod.Alt);

    // Monitor-lie: new model (apps go into their own fullscreen inside a slot we lie about) replacing the strip/crop
    // subsystem. UseLie gates the whole new path; crop stays compiled as a fallback until the lie is proven live.
    static readonly bool UseLie = true;
    static bool lieOn;                                              // the injected hook is installed
    static readonly HashSet<IntPtr> lie = [];                       // windows currently fullscreen into their slot
    static readonly HashSet<string> notCapable = new(StringComparer.OrdinalIgnoreCase); // processes whose F11 didn't fill a slot
    static readonly HashSet<string> capable = new(StringComparer.OrdinalIgnoreCase);     // processes whose F11 did (skip the slow probe)
    static readonly System.Collections.Concurrent.ConcurrentQueue<(IntPtr Hwnd, uint Pid, string Proc, Rect Before, Rect? After, bool Probed)> lieResults = [];
    static readonly Dictionary<string, int> lieMisses = new(StringComparer.OrdinalIgnoreCase); // probes that fullscreened to the real monitor (lie not live yet)
    static readonly Dictionary<IntPtr, Rect> lieSlot = []; // last slot written per window (skip redundant re-sizes)

    // Window changes run off the keyboard hook callback (Windows drops hooks that take too long).
    const uint WM_APP_TOGGLE_CHROME = 0x8001, WM_APP_TOGGLE_REVEAL = 0x8002, WM_APP_MOVED = 0x8004, WM_APP_MOVE_MONITOR = 0x8005, WM_APP_HIGHLIGHT = 0x8006, WM_APP_LIE_RESULT = 0x8007, WM_APP_SNAP = 0x8008, WM_APP_LAYOUTS = 0x800A;
    // Test channel (see tests/wm.ps1): drives the REAL key path and dumps state, because the LL keyboard hook ignores
    // injected keys and a background script can't set foreground - without this every hotkey needed a human.
    const uint WM_APP_TESTKEY = 0x800B, WM_APP_DUMPSTATE = 0x800C, WM_APP_ACTIVATE = 0x800D, WM_APP_REENTER = 0x800E, WM_APP_OPDONE = 0x800F;
    const uint WM_APP_HINTS = 0x8010, WM_APP_HINTKEY = 0x8011; // hint mode: toggle (wParam 1 = hide only), a letter
    const uint WM_APP_HINTSREADY = 0x8013; // worker -> loop: scan done (wParam = dpi)
    const uint WM_APP_INVOKE = 0x8015;     // sandbox: run the queued delegates on the loop thread
    const uint WM_APP_PREFETCH = 0x8017, WM_APP_HINTCACHE = 0x8018; // Alt went down: scan ahead; a background hint scan finished
    const uint WM_APP_TOGGLE_MAX = 0x8016; // Alt+M
    static readonly Dictionary<IntPtr, int> mismatchTicks = []; // lie windows whose rect != slot, consecutive ticks
    // Activation-driven entry is debounced: our own F11 toggles bounce focus through bystander windows for a few
    // hundred ms, and probing each one (measured: Word) stole the foreground from the window being tiled.
    static IntPtr pendingFg;
    const uint ActivateSettleMs = 60; // a window still foreground after this long was really activated (was 600: a restored or activated window sat in its chrome for ~780ms before going borderless)
    static readonly HashSet<IntPtr> probing = []; // capability probe (~1.5s) still in flight: not a MISMATCH yet
    static readonly Dictionary<IntPtr, long> awaitingZone = []; // windows dropped out of fullscreen, waiting for a Snap Layouts pick
    const uint ZoneWaitMs = 10000; // give up waiting for a pick (user cancelled) after this and restore fullscreen
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
        if (args.Contains("--sandbox")) EnterSandbox(args); // hidden-desktop test mode: see Program.Sandbox.cs
        if (args.Contains("--no-prefetch")) prefetchOn = false;
        Log.Init(Log.DefaultDir, args.Contains("--verbose"));
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { Log.Info($"CRASH {e.ExceptionObject}"); CursorTheme.Restore(); };
        Log.Info($"start pid={Environment.ProcessId} args=[{string.Join(' ', args)}]");
        // Real pixel rects for every window on every monitor; without it Windows hands us DPI-virtualized coordinates.
        if (!SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)) Log.Win32("SetProcessDpiAwarenessContext");

        LoadCropOverrides();
        if (args.Contains("--dump")) { Dump(); return 0; }
        if (args.Contains("--hotkeys")) { HelpWindow.ShowAndWait(); return 0; } // the list on its own, no hooks
        if (args.Contains("--lie-test")) { LieTest(args); return 0; }  // exercise the monitor-lie engine on one window

        if (!Desk.Sandbox) { ReplaceRunningInstances(); RecoverFromDeadInstance(); } // a sandbox never touches another instance or its state
        new Thread(() =>
        {
            foreach (var send in outbox.GetConsumingEnumerable())
                try { send(); } catch (Exception e) { Log.Info($"input sender error: {e}"); }
        }) { IsBackground = true, Name = "input-sender" }.Start();
        new Thread(() =>
        {
            foreach (var (name, queued, run) in opbox.GetConsumingEnumerable())
            {
                var waited = queued.ElapsedMilliseconds; var sw = Stopwatch.StartNew();
                try { run(); } catch (Exception e) { Log.Info($"lie worker error: {e}"); }
                if (waited >= SlowWorkerMs || sw.ElapsedMilliseconds >= SlowWorkerMs) Log.Info($"SLOW on the worker: {name} waited {waited}ms, ran {sw.ElapsedMilliseconds}ms");
            }
        }) { IsBackground = true, Name = "lie-worker" }.Start();
        msgWnd = CreateMessageWindow();
        if (msgWnd == IntPtr.Zero) return 1;

        var hook = IntPtr.Zero;
        if (!Desk.Sandbox) // the sandbox is driven through the test channel; a hook there would see nothing
        {
            keyboardProc = KeyboardHook;
            hook = SetWindowsHookEx(WH_KEYBOARD_LL, keyboardProc, GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero) { Log.Win32("SetWindowsHookEx(WH_KEYBOARD_LL)"); return 1; }
        }

        prevActive = Desk.Foreground();
        StartTimer(TimerHover, HoverIntervalMs);
        var mouseHook = IntPtr.Zero;
        if (!Desk.Sandbox && RegisterTrackPoint())
        {
            StartTimer(TimerWatchdog, WatchdogMs);
            mouseProc = MouseHook;
            mouseHook = SetWindowsHookEx(WH_MOUSE_LL, mouseProc, GetModuleHandle(null), 0);
            if (mouseHook == IntPtr.Zero) Log.Win32("SetWindowsHookEx(WH_MOUSE_LL)");
        }

        if (UseLie) lieOn = MonitorLie.Install();

        winEventProc = OnWinEvent;
        var showHook = SetWinEventHook(EVENT_OBJECT_DESTROY, EVENT_OBJECT_SHOW, IntPtr.Zero, winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        var moveHook = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        // Foreground changes: a window not yet fullscreened enters its slot when the user first activates it, so we
        // never mass-steal focus at startup (F11 only reaches the foreground window).
        var fgHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        if (showHook == IntPtr.Zero || moveHook == IntPtr.Zero) Log.Info("SetWinEventHook failed: new windows won't be managed");
        EnumWindows((hwnd, _) => { Manage(hwnd); return true; }, IntPtr.Zero);

        stableDisplays = pendingDisplays = Layout.Signature(Displays());
        lastTick = Environment.TickCount64;
        if (RegisterSuspendResumeNotification(msgWnd, DEVICE_NOTIFY_WINDOW_HANDLE) == IntPtr.Zero)
            Log.Win32("RegisterSuspendResumeNotification (wake still detected by the clock jump)");
        CursorTheme.Apply();

        if (Desk.Sandbox) StartSandbox(args); // pump + macro thread, once the loop below is about to run
        PostLoop(Hints.Warm);                          // first use of the scanner and the overlay costs ~100ms+: pay it now, not on the first Alt+F
        if (prefetchOn) StartTimer(TimerPrefetch, 1500); // and have the foreground window's labels ready from the start

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            var sw = Stopwatch.StartNew();
            DispatchMessage(ref msg);
            Slow(sw, () => $"msg 0x{msg.message:X} wParam={msg.wParam}");
        }

        UnhookWinEvent(showHook);
        UnhookWinEvent(moveHook);
        if (fgHook != IntPtr.Zero) UnhookWinEvent(fgHook);
        HideHints();
        if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
        if (mouseHook != IntPtr.Zero) UnhookWindowsHookEx(mouseHook);
        RestoreAll();
        if (lieOn) MonitorLie.Uninstall();
        CursorTheme.Restore();
        State.Delete(State.DefaultPath); // clean exit: nothing left for a successor to undo
        Log.Info("shutdown");
        if (Desk.Sandbox) FinishSandbox();
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
                    case WM_APP_TOGGLE_CHROME: ToggleChrome(Desk.Foreground()); break;
                    case WM_APP_TOGGLE_REVEAL: ToggleReveal(Desk.Foreground()); break;
                    case WM_APP_MOVED: HandleMovedWindows(); break;
                    case WM_APP_MOVE_MONITOR: MoveToMonitor(Desk.Foreground(), (int)wParam); break;
                    case WM_APP_HIGHLIGHT: Highlight(Desk.Foreground()); break;
                    case WM_APP_LIE_RESULT: HandleLieResults(); break;
                    case WM_APP_SNAP: SnapTile(Desk.Foreground(), (int)wParam); break;
                    case WM_APP_LAYOUTS: OpenLayouts(Desk.Foreground()); break;
                    case WM_APP_TESTKEY: SimulateKey((int)wParam, lParam != IntPtr.Zero); break;
                    case WM_APP_HINTS: ToggleHints(hideOnly: wParam != IntPtr.Zero); break;
                    case WM_APP_HINTKEY: HintKey((int)wParam, lParam != IntPtr.Zero); break;
                    case WM_APP_HINTSREADY: HintsReady((uint)(int)wParam); break;
                    case WM_APP_INVOKE: DrainLoopQueue(); break;
                    case WM_APP_PREFETCH: PrefetchOnAlt(); break;
                    case WM_APP_HINTCACHE: OnHintCache(); break;
                    case WM_APP_TOGGLE_MAX: ToggleMaximizeActive(Desk.Foreground()); break;
                    case WM_APP_OPDONE: // our op on wParam finished: the user acted on it, so it must be the foreground window
                        OpEnd(wParam);
                        if (Volatile.Read(ref opsInFlight) == 0 && wParam != IntPtr.Zero && IsWindow(wParam) && !IsIconic(wParam)
                            && managed.ContainsKey(wParam) && Desk.Foreground() != wParam && Desk.OnCurrentDesktop(wParam))   // never re-assert onto a window the user has left behind on another desktop
                        {
                            Send(KeyEngine.Tap(Vk.Mask)); // counts as input: unlocks SetForegroundWindow (see TestActivate)
                            Log.Info($"op done: re-asserting foreground on {Describe(wParam)} (was {Describe(Desk.Foreground())}) {(Activate(wParam) ? "ok" : "REFUSED")}");
                        }
                        // A Win+Arrow that arrived while this op ran: apply only the LATEST one, now, from fresh state.
                        if (pendingSnap.Remove(wParam, out var nextVk)) { Log.Info($"snap {Describe(wParam)}: applying coalesced vk=0x{nextVk:X}"); SnapTile(wParam, nextVk); }
                        if (pendingToggle.Remove(wParam) && IsWindow(wParam)) { Log.Info($"reveal {Describe(wParam)}: applying the remembered press"); ToggleRevealLie(wParam); }
                        break;
                    case WM_APP_DUMPSTATE: DumpState((long)wParam); break;
                    case WM_APP_ACTIVATE: TestActivate(wParam); break;
                    case WM_APP_REENTER: // a probe that missed the lie: try again now that the mapping is open
                        if (managed.ContainsKey(wParam) && IsWindow(wParam) && !IsIconic(wParam) && !lie.Contains(wParam) && !notCapable.Contains(ProcessName(wParam)))
                            EnterLie(wParam);
                        break;
                    case WM_POWERBROADCAST: OnPower((int)wParam); break;
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
        var sw = Stopwatch.StartNew();
        var r = KeyboardHookCore(nCode, wParam, lParam);
        Slow(sw, () => $"keyboard hook vk=0x{Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam).vkCode:X2}");
        return r;
    }

    static IntPtr KeyboardHookCore(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            var down = (int)wParam is WM_KEYDOWN or WM_SYSKEYDOWN;
            var injected = (k.flags & LLKHF_INJECTED) != 0;
            Log.Debug($"key vk=0x{k.vkCode:X2} {(down ? "down" : "up")}{(injected ? " injected" : "")}");

            if (down && !injected && k.vkCode == 0x09 && AltHeld) altTabbed = true;
            if (down && !injected && k.vkCode is Vk.LAlt or Vk.RAlt && !AltHeld) { Desk.GetCursor(out altDownPos); prevActive = Desk.Foreground(); PostMessage(msgWnd, WM_APP_PREFETCH, IntPtr.Zero, IntPtr.Zero); }
            if (!injected && RouteHint((int)k.vkCode, down)) return 1; // F toggles hints / letters pick / Esc hides
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
            case Act.ToggleHints:
                PostMessage(msgWnd, WM_APP_HINTS, IntPtr.Zero, IntPtr.Zero);
                break;
            case Act.ToggleReveal:
                PostMessage(msgWnd, WM_APP_TOGGLE_REVEAL, IntPtr.Zero, IntPtr.Zero);
                break;
            case Act.MoveToMonitor:
                PostMessage(msgWnd, WM_APP_MOVE_MONITOR, arg, IntPtr.Zero);
                break;
            case Act.SnapLeft or Act.SnapRight or Act.SnapUp or Act.SnapDown:
                var dir = act switch { Act.SnapLeft => Vk.Left, Act.SnapRight => Vk.Right, Act.SnapUp => Vk.Up, _ => Vk.Down };
                PostMessage(msgWnd, WM_APP_SNAP, (IntPtr)dir, IntPtr.Zero);
                break;
            case Act.SnapLayouts:
                PostMessage(msgWnd, WM_APP_LAYOUTS, IntPtr.Zero, IntPtr.Zero);
                break;
            case Act.HighlightWindow:
                PostMessage(msgWnd, WM_APP_HIGHLIGHT, IntPtr.Zero, IntPtr.Zero);
                break;
            case Act.ShowHelp:
                ShowHelp();
                break;
            case Act.Minimize:
                // Same path as Win+Down: a fullscreen window must leave fullscreen (waited) before it is minimized, or the
                // manager keeps counting a minimized window as fullscreen-in-slot (measured by the sandbox: iconic-in-lie).
                PostMessage(msgWnd, WM_APP_SNAP, (IntPtr)Vk.Down, IntPtr.Zero);
                break;
            case Act.ToggleMaximize:
                PostMessage(msgWnd, WM_APP_TOGGLE_MAX, IntPtr.Zero, IntPtr.Zero);
                break;
            case Act.Close:
                // Posted, like clicking the caption button; a hung app can't block the hook.
                var fg = Desk.Foreground();
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
            case Act.DesktopLeft or Act.DesktopRight when !Desk.Sandbox: // the sandbox records the keystroke instead
                Inject(() =>
                {
                    if (VirtualDesktops.Switch(act == Act.DesktopRight)) return;
                    Send([.. KeyEngine.Lift(held), .. KeyEngine.Output(act), .. KeyEngine.Restore(held, keys.Held)]);
                });
                break;
            default:
                Inject(() => Send([.. KeyEngine.Lift(held), .. KeyEngine.Output(act), .. KeyEngine.Restore(held, keys.Held)]));
                break;
        }
    }

    // Stall finder: anything on the loop thread over SlowMs is logged with what it was. Every "it lags" so far was one
    // synchronous call nobody suspected; this names it instead of guessing.
    const long SlowMs = 100, SlowWorkerMs = 1000; // a lie transition legitimately takes ~250ms on the worker
    static void Slow(Stopwatch sw, Func<string> what) { if (sw.ElapsedMilliseconds >= SlowMs) Log.Info($"SLOW {sw.ElapsedMilliseconds}ms on the loop: {what()}"); }

    static void StartTimer(UIntPtr id, uint ms)
    {
        if (SetTimer(msgWnd, id, ms, IntPtr.Zero) == UIntPtr.Zero) Log.Win32($"SetTimer({id})");
    }

    static void OnTimer(UIntPtr id)
    {
        if (id == TimerHover)
        {
            CheckDisplays();
            HoverFocus();
            MaybeRefreshHints();
            if (UseLie && !settling) CheckLieInvariants();
            if (++hoverTicks % CursorCheckTicks == 0 && !settling) CursorTheme.EnsureApplied("periodic check");
        }
        else if (id == TimerZone)
        {
            KillTimer(msgWnd, id);
            foreach (var hwnd in awaitingZone.Keys.ToArray()) if (Environment.TickCount64 >= awaitingZone[hwnd]) ResolveZone(hwnd);
            // The timer used to fire once, a few ms before the deadline, find nothing expired and never run again: the window stayed out of
            // fullscreen for good (measured by the sandbox). Keep checking while anything still waits.
            if (awaitingZone.Count > 0) StartTimer(TimerZone, 250);
        }
        else if (id == TimerActivate)
        {
            KillTimer(msgWnd, id);
            var h = pendingFg; pendingFg = IntPtr.Zero;
            if (Volatile.Read(ref opsInFlight) > 0) return; // a bounce during our own op, not the user: re-checked on WM_APP_OPDONE
            if (h != IntPtr.Zero && Desk.Foreground() == h) MaybeEnterLie(h); // still foreground: a real activation
        }
        else if (id == TimerPrefetch) { KillTimer(msgWnd, id); PrefetchHints("foreground settled"); }
        else if (id == TimerSettle) { KillTimer(msgWnd, id); RepairMoveBack(); StartTimer(TimerRepair, RepairDelayMs); }
        else if (id == TimerRepair) { KillTimer(msgWnd, id); RepairRecrop(); }
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
        var now = Desk.Foreground();
        if (altTabbed)
        {
            altTabbed = false;
            if (now != IntPtr.Zero && !IsIconic(now) && !IsOurs(now)) { Log.Info($"alt+tab landed on {Describe(now)}: highlight"); Highlight(now, SwitchHighlightMs); }
        }
        if (now != IntPtr.Zero && IsIconic(now)) { prevActive = now; return; }   // a minimized window is parked at -32000,-32000: centring on it threw the cursor off screen (real log)
        Desk.GetCursor(out var pt);
        var inside = GetWindowRect(now, out var r) && r.Contains(pt.X, pt.Y);
        if (Focus.ShouldCenter(prevActive, now, inside) && r.Width > 0 && r.Height > 0)
        {
            var (cx, cy) = r.Center;
            if (Desk.SetCursor(cx, cy)) Log.Info($"center cursor on {Describe(now)} at {cx},{cy}");
            else Log.Win32("Desk.SetCursor(center)");
        }
        prevActive = now;
    }

    static void HoverFocus()
    {
        if (AltHeld) return;
        Desk.GetCursor(out var pt);
        var moved = pt.X != lastHoverPos.X || pt.Y != lastHoverPos.Y; lastHoverPos = pt;
        var hwnd = GetAncestor(WindowFromPoint(pt), GA_ROOT);
        var real = Focus.IsRealWindow((uint)GetWindowLong(hwnd, GWL_STYLE));
        if (!Focus.ShouldHoverFocus(hwnd, lastHover, false, real, moved)) return;
        lastHover = hwnd;
        Log.Info($"hover focus {Describe(hwnd)} {(Activate(hwnd) ? "ok" : "REFUSED")}");
    }

    // Like AHK's WinActivate: plain SetForegroundWindow, and if the foreground lock refuses,
    // borrow the foreground thread's input state and retry.
    static bool Activate(IntPtr hwnd)
    {
        if (Desk.SetForeground(hwnd)) return true;
        var fgThread = GetWindowThreadProcessId(Desk.Foreground(), out _);
        var me = GetCurrentThreadId();
        AttachThreadInput(me, fgThread, true);
        BringWindowToTop(hwnd);
        var ok = Desk.SetForeground(hwnd);
        AttachThreadInput(me, fgThread, false);
        return ok;
    }

    // Cached per window: looking the process up from scratch on every log line enumerates processes, which took 80-400ms on a busy machine
    // and showed up as the manager's loop being unavailable (sandbox: loop-stall).
    static string Describe(IntPtr hwnd) => $"0x{hwnd:X}({ProcessName(hwnd)})";

    // ---- borderless windows ----

    static void Manage(IntPtr hwnd)
    {
        if (managed.ContainsKey(hwnd)) return;
        var style = Frames.Style(hwnd);
        var cls = Frames.ClassName(hwnd);
        if (!Chrome.IsManageable(style, Frames.ExStyle(hwnd), cls, ProcessName(hwnd))) return;
        if (IsIconic(hwnd)) return; // classification needs real geometry; picked up on its next location change

        if (UseLie)
        {
            managed[hwnd] = style;
            Log.Info($"manage {Describe(hwnd)} class={cls} style=0x{style:X8}");
            GetWindowThreadProcessId(hwnd, out var lpid);
            if (lieOn) MonitorLie.Touch(lpid); // mapping exists now, long before any probe (see Touch)
            RecordLayout(hwnd);
            MaybeEnterLie(hwnd); // only if it's the foreground window; otherwise on its next activation
            return;
        }

        // Classify before stripping: once WS_CAPTION is gone every window would look self-drawn.
        GetWindowRect(hwnd, out var window);
        var dpi = GetDpiForWindow(hwnd);
        var selfDrawn = Crop.IsSelfDrawn(window, Frames.ClientOnScreen(hwnd), dpi);
        if (selfDrawn && !Crop.ShouldCrop(selfDrawn, cls)) { Log.Info($"uwp {Describe(hwnd)}: not cropped"); selfDrawn = false; }

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
        RecordLayout(hwnd);
    }

    // ---- hint mode (Alt+F): decisions in HintEngine (Logic.cs), targets / overlay / clicks in Hints.cs ----

    // Alt+F (the toggle) is a key-engine binding (Act.ToggleHints): it masks the Alt release, so Word's KeyTips and
    // every app's menu bar stay shut, and plain F types everywhere. This runs on the hook thread, before the engine.
    // While hints are up: a letter picks (Shift held = Shift-pick, badges stay), Esc hides, everything else passes.
    static bool RouteHint(int vk, bool down)
    {
        var mods = keys.Mods;
        var chord = (mods & (Mod.Ctrl | Mod.Alt | Mod.Win)) != 0;
        if (!hints.Active) return false;
        if (vk == Vk.Esc) { if (down) PostMessage(msgWnd, WM_APP_HINTS, (IntPtr)1, IntPtr.Zero); return true; }
        if (vk is >= 'A' and <= 'Z' && !chord)
        {
            if (down) PostMessage(msgWnd, WM_APP_HINTKEY, (IntPtr)vk, (IntPtr)(mods.HasFlag(Mod.Shift) ? 1 : 0));
            return true;
        }
        return false;
    }

    static bool IsOurs(IntPtr hwnd) { GetWindowThreadProcessId(hwnd, out var pid); return pid == Environment.ProcessId; }

    // ---- hint prefetch: the labels are worked out BEFORE Alt+F ----
    // A scan costs 350-800ms on real windows (measured in the live log), and doing it after the key made Alt+F feel slow. So it is done
    // ahead of time: when the foreground window settles, the moment Alt goes down, after a pick, and every few seconds while the user is
    // working. Alt+F then draws from the finished scan, or - if it lands mid-scan - waits for the rest of that one instead of starting over.
    // A finished scan is trusted only while the same windows are on screen at the same rectangles (HintCache.Usable). Off: --no-prefetch.
    // Side effect to know: Chromium and Electron apps switch their accessibility tree on when a UI Automation client first asks, which
    // costs them a little speed; prefetching does that when you first use an app, not when you first press Alt+F in it.
    sealed record HintSnapshot(IntPtr Fg, List<(long Hwnd, Rect Rect)> Windows, List<Hints.Target> Targets, uint Dpi, long TakenAt);
    static volatile HintSnapshot? hintCache;
    static int hintPrefetching;      // 1 while a background scan runs (one at a time)
    static bool hintPrefetchAgain;   // a trigger arrived while it ran: scan once more afterwards
    static bool hintWanted;          // Alt+F was pressed while the background scan ran: show it the moment it lands
    static bool prefetchOn = true;
    const uint PrefetchSettleMs = 250;

    static List<IntPtr> CollectHintWindows(out IntPtr fg, out uint dpi)
    {
        fg = Desk.Foreground();
        // Only the focused window is labelled (user request): fewer badges, shorter labels, a much cheaper scan.
        var windows = new List<IntPtr>();
        if (fg != IntPtr.Zero && !IsOurs(fg) && !IsIconic(fg)) windows.Add(fg);
        dpi = fg != IntPtr.Zero ? GetDpiForWindow(fg) : 96u;
        return windows;
    }

    static List<(long Hwnd, Rect Rect)> RectsOf(List<IntPtr> windows) =>
        [.. windows.Select(h => ((long)h, GetWindowRect(h, out var r) ? r : default))];

    static HintSnapshot? UsableHintCache()
    {
        var s = hintCache;
        if (s is null) return null;
        var windows = CollectHintWindows(out var fg, out _);
        return HintCache.Usable(Environment.TickCount64 - s.TakenAt, fg == s.Fg, s.Windows, RectsOf(windows)) ? s : null;
    }

    static long MsSinceInput()
    {
        var li = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        return GetLastInputInfo(ref li) ? unchecked((uint)(GetTickCount() - li.dwTime)) : 0;
    }

    // The scan itself, off the loop and off the lie-worker (a scan behind a fullscreen transition made F wait for it).
    static void StartHintScan(bool prefetch)
    {
        var windows = CollectHintWindows(out var fg, out var dpi);
        if (windows.Count == 0)
        {
            if (prefetch) Interlocked.Exchange(ref hintPrefetching, 0);
            else { hintScanning = false; Log.Info("hints: nothing clickable on screen"); }
            return;
        }
        var rects = RectsOf(windows);
        var queued = Stopwatch.StartNew();
        Task.Run(() =>
        {
            try
            {
                if (queued.ElapsedMilliseconds >= SlowMs) Log.Info($"SLOW hint scan start: waited {queued.ElapsedMilliseconds}ms for a thread");
                var targets = Hints.Scan(windows, log: !prefetch);
                hintCache = new HintSnapshot(fg, rects, targets, dpi, Environment.TickCount64);
                if (!prefetch) hintScans.Enqueue(targets);
            }
            catch (Exception e)
            {
                Log.Info($"hint scan failed: {e.Message}");
                if (!prefetch) hintScans.Enqueue([]);
            }
            finally
            {
                if (prefetch) Interlocked.Exchange(ref hintPrefetching, 0);
                PostMessage(msgWnd, prefetch ? WM_APP_HINTCACHE : WM_APP_HINTSREADY, (IntPtr)(int)dpi, IntPtr.Zero);
            }
        });
    }

    // why = "refresh after show" is the one reason that runs while badges are up (to catch a change; see OnHintCache).
    static void PrefetchHints(string why)
    {
        if (!prefetchOn || settling || (hintScanning && !hintWanted)) return;
        if (hints.Active && why != "refresh after show") return;
        if (Interlocked.CompareExchange(ref hintPrefetching, 1, 0) != 0) { hintPrefetchAgain = true; return; }
        Log.Debug($"hint prefetch ({why})");
        StartHintScan(prefetch: true);
    }

    // Alt just went down: Alt+F is probably next. Scan now unless a scan from the last second is still good.
    static void PrefetchOnAlt()
    {
        // A usable scan, whatever its age, is what Alt+F will draw from (and it is refreshed after it is shown). Scanning here would make the F
        // that follows ~15ms later WAIT for that new scan (measured 658ms instead of ~0).
        if (UsableHintCache() is not null) return;
        PrefetchHints("alt down");
    }

    // On the 700ms tick: keep the scan fresh while the user is working, so a page that changed since the last scan is covered.
    static void MaybeRefreshHints()
    {
        if (!prefetchOn || hints.Active || hintScanning || settling || Volatile.Read(ref hintPrefetching) == 1 || Volatile.Read(ref opsInFlight) > 0) return;
        var age = hintCache is { } c ? Environment.TickCount64 - c.TakenAt : -1;
        if (HintCache.WantsRefresh(age, MsSinceInput())) PrefetchHints("periodic");
    }

    static void ShowHintTargets(List<Hints.Target> targets, uint dpi)
    {
        hintTargets = targets;
        hints.Show(targets.Count);
        var sw = Stopwatch.StartNew();
        Hints.Show(targets, hints.Labels, dpi);
        if (sw.ElapsedMilliseconds >= 25) Log.Info($"hints: drawn in {sw.ElapsedMilliseconds}ms");   // so a slow draw is seen, not guessed
    }

    static void ShowHints(HintSnapshot s, string how)
    {
        Log.Info($"hints: {s.Targets.Count} targets shown from the {how} scan (age {Environment.TickCount64 - s.TakenAt}ms)");
        ShowHintTargets(s.Targets, s.Dpi);
    }

    static void ToggleHints(bool hideOnly)
    {
        if (hints.Active || hideOnly) { HideHints(); return; }
        if (hintScanning) { hintCancel = true; hintWanted = false; return; } // pressed again while waiting = never mind
        if (UsableHintCache() is { } s && s.Targets.Count > 0)
        {
            ShowHints(s, "prefetched");
            PrefetchHints("refresh after show");   // the page may have changed since: look again, redraw only if it did
            return;
        }
        hintScanning = true; hintCancel = false;
        if (Volatile.Read(ref hintPrefetching) == 1) { hintWanted = true; Log.Info("hints: waiting for the scan already in progress"); return; }
        StartHintScan(prefetch: false);
    }

    // A user-requested scan finished.
    static void HintsReady(uint dpi)
    {
        hintScanning = false;
        if (!hintScans.TryDequeue(out var targets)) return;
        if (hintCancel) { hintCancel = false; return; }
        if (targets.Count == 0) { Log.Info("hints: nothing clickable on screen"); return; }
        ShowHintTargets(targets, dpi);
    }

    // A background scan finished.
    static void OnHintCache()
    {
        if (hintWanted)   // Alt+F was pressed while it ran
        {
            hintWanted = false; hintScanning = false;
            if (hintCancel) { hintCancel = false; return; }
            if (UsableHintCache() is { } s) { if (s.Targets.Count > 0) ShowHints(s, "just-finished"); else Log.Info("hints: nothing clickable on screen"); }
            else { hintScanning = true; StartHintScan(prefetch: false); }   // what it scanned is no longer what is on screen
        }
        else if (hints.Active && hints.Typed.Length == 0 && hintCache is { Targets.Count: > 0 } c && UsableHintCache() is not null
                 && HintCache.ShouldRedraw(true, [.. hintTargets.Select(t => t.Bounds)], [.. c.Targets.Select(t => t.Bounds)]))
        {
            Log.Info($"hints: the page changed while the badges were up, redrawn ({c.Targets.Count} targets)");
            ShowHintTargets(c.Targets, c.Dpi);
        }
        if (hintPrefetchAgain) { hintPrefetchAgain = false; PrefetchHints("trigger during scan"); }
    }

    static void HideHints() { hints.Hide(); Hints.Hide(); hintTargets = []; }

    static void HintKey(int vk, bool shift)
    {
        if (!hints.Active) return;
        var (step, i) = hints.Type((char)(vk + 32), shift); // 'A'..'Z' -> 'a'..'z'
        switch (step)
        {
            case HintEngine.Step.Narrowed or HintEngine.Step.Reset: Hints.Redraw(hints.Dim); break;
            case HintEngine.Step.Pick: { var t = hintTargets[i]; HideHints(); ClickTarget(t, false); hintCache = null; StartTimer(TimerPrefetch, 700); break; }   // the click changes the page: scan it again shortly

            case HintEngine.Step.PickKeep: Hints.Redraw(_ => false); ClickTarget(hintTargets[i], true); break;
        }
    }

    // A real click where the element is (the web version's el.click()): on the sender thread, never the hook thread.
    static void ClickTarget(Hints.Target t, bool shift)
    {
        Log.Info($"hints: click {t.ClickX},{t.ClickY}{(shift ? " shift" : "")}");
        var held = keys.Held;
        Inject(() =>
        {
            Send(KeyEngine.Lift(held)); // a physically held Shift (Shift-pick) is added back around the click below
            Desk.SetCursor(t.ClickX, t.ClickY);
            Thread.Sleep(20);
            if (shift) Send([new KeyStroke(Vk.LShift, false)]);
            SendMouse(MouseInput(MOUSEEVENTF_LEFTDOWN), MouseInput(MOUSEEVENTF_LEFTUP));
            if (shift) Send([new KeyStroke(Vk.LShift, true)]);
            Send(KeyEngine.Restore(held, keys.Held));
        });
    }

    // ---- test channel ----

    // The same path a physical key takes through KeyboardHook (KeyEngine.Feed + Run), minus the mask injection.
    // Lets a script exercise every hotkey end-to-end; the hook itself drops injected keys on purpose.
    static void SimulateKey(int vk, bool down)
    {
        if (Desk.Sandbox) NoteKeyDispatched();
        if (down && vk == 0x09 && AltHeld) altTabbed = true;
        if (down && !AltHeld && vk is Vk.LAlt or Vk.RAlt) { Desk.GetCursor(out altDownPos); prevActive = Desk.Foreground(); PostMessage(msgWnd, WM_APP_PREFETCH, IntPtr.Zero, IntPtr.Zero); }
        if (RouteHint(vk, down)) { Log.Debug($"testkey vk=0x{vk:X2} {(down ? "down" : "up")} -> hints"); return; }
        var d = keys.Feed(vk, down, false, (uint)Environment.TickCount);
        if (Desk.Sandbox) SandboxKeyOutcome(vk, down, d); // what the app would see: the passed-through key, or the mask+release the real hook injects
        if (d.Act != Act.None) Run(d.Act, d.Arg);
        if (!down && vk is Vk.LAlt or Vk.RAlt) OnAltReleased();
        Log.Debug($"testkey vk=0x{vk:X2} {(down ? "down" : "up")} -> {d.Act}");
    }

    // Windows' foreground lock refuses SetForegroundWindow to a process without recent input. An injected tap of the
    // unassigned mask key counts as input (our hook ignores injected keys, apps ignore 0xE8), which unlocks it.
    // Retried, because the first attempt in a scenario was REFUSED and every later assertion then hit the wrong window.
    static void TestActivate(IntPtr hwnd)
    {
        if (IsIconic(hwnd)) { ShowWindow(hwnd, SW_RESTORE); Thread.Sleep(250); } // activating = a taskbar click: un-minimize first
        if (Desk.Sandbox) { Desk.SetForeground(hwnd); Log.Info($"test activate {Describe(hwnd)} ok (virtual foreground)"); return; }
        for (var i = 0; i < 4; i++)
        {
            Send(KeyEngine.Tap(Vk.Mask));
            if (Activate(hwnd) && Desk.Foreground() == hwnd) { Log.Info($"test activate {Describe(hwnd)} ok"); return; }
            Thread.Sleep(150);
        }
        Log.Info($"test activate {Describe(hwnd)} REFUSED");
    }

    // Everything a test needs to assert on, as JSON: what we THINK (lie, slot, revealed, capability) next to what
    // Windows SAYS (rect, visible frame, zoomed, foreground). A mismatch between the two columns is the bug.
    public static string StatePath => Path.Combine(Desk.Sandbox ? Log.DefaultDir : Path.GetTempPath(), "wm-state.json");

    static void DumpState(long seq) // seq: echoed in the JSON so the harness knows THIS request was answered
    {
        var fg = Desk.Foreground();
        var windows = managed.Keys.Where(IsWindow).Select(h =>
        {
            GetWindowRect(h, out var r);
            return new
            {
                hwnd = $"0x{h:X}", proc = ProcessName(h), title = Frames.Title(h),
                lie = lie.Contains(h), slot = lieSlot.TryGetValue(h, out var s) ? s.ToString() : null,
                rect = r.ToString(), frame = Frames.FrameBounds(h)?.ToString(),
                zoomed = IsZoomed(h), iconic = IsIconic(h), foreground = h == fg,
                revealed = revealed.Contains(h), awaitingZone = awaitingZone.ContainsKey(h),
                fillsSlot = lieSlot.TryGetValue(h, out var s2) && LieLogic.FilledSlot(r, s2),
            };
        }).ToArray();
        var hintState = new { active = hints.Active, typed = hints.Typed, targets = hintTargets.Count };
        var state = new { seq, foreground = $"0x{fg:X}", capable, notCapable, hints = hintState, windows };
        File.WriteAllText(StatePath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
    }

    // Self-check on the 700ms tick: a lie window whose rect has not matched its slot for 2 consecutive ticks is a bug
    // in progress. Logged once per episode so `grep MISMATCH wm.log` is the troubleshooting entry point.
    static void CheckLieInvariants()
    {
        foreach (var h in lie.ToArray())
        {
            // In transition (probe or any lie op in flight): not judged, and the streak restarts afterwards - otherwise
            // back-to-back ops (rapid Win+Arrow) accumulate ticks and a healthy window gets flagged mid-flight.
            if (probing.Contains(h) || OpActive(h) || !IsWindow(h) || IsIconic(h)) { mismatchTicks.Remove(h); continue; }
            if (!lieSlot.TryGetValue(h, out var slot) || !GetWindowRect(h, out var r)) continue;
            if (LieLogic.FilledSlot(r, slot)) { mismatchTicks.Remove(h); continue; }
            var n = mismatchTicks[h] = mismatchTicks.GetValueOrDefault(h) + 1;
            if (n == 2) Log.Info($"lie MISMATCH {Describe(h)} rect={r} slot={slot} zoomed={IsZoomed(h)}");
        }
    }

    // ---- monitor lie ----

    // Put a managed window into its slot, but only when it's the foreground window (F11 reaches only the foreground).
    static void MaybeEnterLie(IntPtr hwnd)
    {
        if (!lieOn || settling || !managed.ContainsKey(hwnd) || IsIconic(hwnd)) return;
        if (lie.Contains(hwnd) || revealed.Contains(hwnd) || userShown.Contains(hwnd)) return;
        if (notCapable.Contains(ProcessName(hwnd))) return; // app has no usable fullscreen; left with its own chrome
        if (Desk.Foreground() != hwnd) return;          // wait until the user activates it
        EnterLie(hwnd);
    }

    // The slot an app should fullscreen into = exactly what the user sees it occupying: the whole monitor when
    // maximized, otherwise its VISIBLE frame (not the window rect, which includes invisible borders).
    static Rect SlotOf(IntPtr hwnd, out uint pid)
    {
        GetWindowThreadProcessId(hwnd, out pid);
        var monitor = Frames.MonitorRect(hwnd);
        if (IsZoomed(hwnd)) return monitor;
        GetWindowRect(hwnd, out var window);
        var slot = LieLogic.Clamp(Frames.FrameBounds(hwnd) ?? window, monitor); // never an off-screen slot
        return slot.Width > 0 && slot.Height > 0 ? slot : monitor;   // a window left wholly off every monitor gets the monitor, not an empty slot
    }

    // The lie is keyed by (process, monitor): one entry per monitor a process has a managed window on. Each window owns at most one
    // entry; two windows of one process on the SAME monitor with different slots share it (the last one set wins, logged).
    static readonly Dictionary<IntPtr, (uint Pid, IntPtr Mon)> lieEntry = [];
    static readonly Dictionary<(uint Pid, IntPtr Mon), IntPtr> lieOwner = [];

    static void LieSet(IntPtr hwnd, Rect slot)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        var mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (lieEntry.TryGetValue(hwnd, out var old) && old.Mon != mon && lieOwner.GetValueOrDefault(old) == hwnd) { lieOwner.Remove(old); MonitorLie.Disable(old.Pid, old.Mon); } // it moved to another monitor
        var key = (pid, mon);
        if (lieOwner.TryGetValue(key, out var other) && other != hwnd && IsWindow(other) && lie.Contains(other))
            Log.Info($"lie conflict: {Describe(hwnd)} and {Describe(other)} are on the same monitor in one process; the slot of {Describe(hwnd)} wins (one slot per monitor per process)");
        lieEntry[hwnd] = key; lieOwner[key] = hwnd;
        MonitorLie.SetSlot(pid, mon, slot);
    }

    static void LieClear(IntPtr hwnd)
    {
        if (!lieEntry.Remove(hwnd, out var e)) return;
        if (lieOwner.GetValueOrDefault(e) != hwnd) return;   // another window of the process took this monitor over; its lie stands
        lieOwner.Remove(e);
        MonitorLie.Disable(e.Pid, e.Mon);
    }

    static void LieDisableProcess(uint pid)
    {
        foreach (var (h, e) in lieEntry.Where(kv => kv.Value.Pid == pid).ToArray()) { lieEntry.Remove(h); lieOwner.Remove(e); }
        MonitorLie.DisableAll(pid);
    }

    // Is the app in its own fullscreen right now - at the real monitor, or at the slot we lied to it about?
    // Every F11 we send is decided from this, never assumed (F11 toggles). Loop thread only (reads lieSlot).
    static bool InAppFullscreen(IntPtr hwnd)
    {
        if (Chrome.HasCaption(Frames.Style(hwnd))) return false;   // with its title bar showing an app is NOT in its own fullscreen, whatever its rectangle (a maximized-looking window equals the monitor)
        if (!GetWindowRect(hwnd, out var r)) return false;
        return LieLogic.IsAppFullscreen(r, Frames.MonitorRect(hwnd))
            || (lieSlot.TryGetValue(hwnd, out var s) && LieLogic.IsAppFullscreen(r, s));
    }

    static void EnterLie(IntPtr hwnd)
    {
        if (IsIconic(hwnd)) { Log.Info($"lie enter {Describe(hwnd)} skipped: it is minimized"); return; }   // F11 to a minimized window does nothing useful and read its parked rect as the slot
        var proc = ProcessName(hwnd);
        var fsNow = InAppFullscreen(hwnd); // BEFORE the slot changes: F11 toggles, so count from the real state
        var oldSlot = lieSlot.GetValueOrDefault(hwnd); // where it is fullscreen now, if it is (for the leave-wait)
        var monitor = Frames.MonitorRect(hwnd);
        var slot = SlotOf(hwnd, out var pid);
        LieSet(hwnd, slot);
        lieSlot[hwnd] = slot;
        lie.Add(hwnd); // optimistic: a failed probe takes it back out in HandleLieResults
        var probe = !capable.Contains(proc);
        if (probe) probing.Add(hwnd);
        GetWindowRect(hwnd, out var before);
        Log.Info($"lie enter {Describe(hwnd)} slot={slot}{(probe ? " (probe)" : "")}{(fsNow ? " (already fullscreen: toggling out first)" : "")}");
        // Off the message loop (TryFullscreen sleeps ~1.5s): the input-sender thread serialises it with other SendInput.
        Op(hwnd, () =>
        {
            if (!MonitorLie.Focus(hwnd)) { AbandonEnter(hwnd, pid); return; }   // the user already moved to another desktop: do nothing, drag nobody back
            if (fsNow) MonitorLie.LeaveFullscreen(hwnd, oldSlot, monitor); // waits until it HAS left, so the next F11 enters
            Rect? after;
            if (probe) after = MonitorLie.TryFullscreen(hwnd, slot, monitor);
            else { MonitorLie.EnterFullscreen(hwnd, slot, monitor); after = GetWindowRect(hwnd, out var ar) ? ar : null; } // verified by the verdict, never assumed
            // Gone to another desktop mid-way and the window did not get there: undo the optimistic state instead of judging a half-made transition.
            if (!Desk.OnCurrentDesktop(hwnd) && !(after is { } fa && LieLogic.FilledSlot(fa, slot))) { AbandonEnter(hwnd, pid); return; }
            lieResults.Enqueue((hwnd, pid, proc, before, after, probe));
            PostMessage(msgWnd, WM_APP_LIE_RESULT, IntPtr.Zero, IntPtr.Zero);
        });
    }

    // The user moved to another virtual desktop before an entry into fullscreen began, or it came to nothing: undo the optimistic state
    // EnterLie set, so nothing is left half-made and nothing counts the app as incapable. Runs on the loop.
    static void AbandonEnter(IntPtr hwnd, uint pid)
    {
        Log.Info($"lie enter {Describe(hwnd)} abandoned: the window is on another virtual desktop now");
        PostLoop(() => { probing.Remove(hwnd); lie.Remove(hwnd); lieSlot.Remove(hwnd); LieClear(hwnd); });
    }

    static void HandleLieResults()
    {
        while (lieResults.TryDequeue(out var r))
        {
            probing.Remove(r.Hwnd); // probe finished, whatever the verdict: the invariant check may judge it now
            // Non-probe enters are verified by the same verdict as probes: "lie ok" is never assumed (measured: it was
            // logged on a window that had restored from minimize as a normal window, and the F11 toggled it wrong).
            var slot = lieSlot.GetValueOrDefault(r.Hwnd);
            var verdict = r.After is { } a && IsWindow(r.Hwnd)
                ? LieLogic.ProbeVerdict(r.Before, a, slot, Frames.MonitorRect(r.Hwnd))
                : LieLogic.Probe.NotCapable;
            switch (verdict)
            {
                case LieLogic.Probe.Capable:
                    capable.Add(r.Proc); lieMisses.Remove(r.Proc);
                    Log.Info($"lie ok {Describe(r.Hwnd)} ({r.Proc})");
                    break;
                case LieLogic.Probe.LieMissed when lieMisses.GetValueOrDefault(r.Proc) < 2:
                case LieLogic.Probe.Landed when lieMisses.GetValueOrDefault(r.Proc) < 1:
                case LieLogic.Probe.NotCapable when !r.Probed && lieMisses.GetValueOrDefault(r.Proc) < 2: // known-capable app: F11 swallowed or toggled wrong, not incapacity
                    // It DID fullscreen (so it is capable) but into the real monitor: the lie wasn't live in that
                    // process yet. Toggle it back out so the state is known, then re-enter once the mapping is open.
                    lieMisses[r.Proc] = lieMisses.GetValueOrDefault(r.Proc) + 1;
                    lie.Remove(r.Hwnd); lieSlot.Remove(r.Hwnd);
                    Log.Info($"lie missed {Describe(r.Hwnd)} ({r.Proc}): fullscreened to {r.After}, not slot {slot}; retrying");
                    var h = r.Hwnd;
                    // No blind F11 here: EnterLie now reads the real fullscreen state and toggles out itself if needed.
                    Op(h, () => { Thread.Sleep(400); PostMessage(msgWnd, WM_APP_REENTER, h, IntPtr.Zero); });
                    break;
                default:
                    lie.Remove(r.Hwnd); lieSlot.Remove(r.Hwnd);
                    if (r.Probed && verdict != LieLogic.Probe.Landed) { notCapable.Add(r.Proc); LieDisableProcess(r.Pid); Log.Info($"lie not-capable {r.Proc}: F11 left it at {r.After} (was {r.Before}), leaving its chrome"); }
                    else Log.Info($"lie enter {Describe(r.Hwnd)} ({r.Proc}) ended at {r.After}, not slot {slot}: left out, next activation retries");
                    // An app that ignored the lie fullscreened onto the REAL monitor and would stay there, unmanaged (measured by the sandbox:
                    // appfs=true, lie=false). Put it back to a normal window so nothing is left in a state we do not know about.
                    if (IsWindow(r.Hwnd) && !IsIconic(r.Hwnd) && GetWindowRect(r.Hwnd, out var stuck)
                        && (LieLogic.IsAppFullscreen(stuck, Frames.MonitorRect(r.Hwnd)) || (r.After is { } moved && !Invariants.Near(moved, r.Before, 2) && !Chrome.HasCaption(Frames.Style(r.Hwnd)))))   // on the monitor, or borderless at a size that is not our slot (an app's own minimum size)
                    {
                        var h2 = r.Hwnd; var mon2 = Frames.MonitorRect(h2);
                        Log.Info($"lie: {Describe(h2)} is fullscreen on the real monitor and not managed; leaving its fullscreen");
                        Op(h2, () => { Desk.SetForeground(h2); MonitorLie.LeaveFullscreen(h2, default, mon2); });
                    }
                    break;
            }
        }
    }

    // A fullscreen window moved (snap / maximize toggle / monitor move): give it the new slot and have it re-read,
    // which re-sizes it in place without leaving fullscreen. Skips when the slot is unchanged (our own echo).
    static void UpdateLieSlot(IntPtr hwnd, bool force = false)
    {
        if (!lie.Contains(hwnd) || !IsWindow(hwnd) || IsIconic(hwnd)) return;
        var slot = SlotOf(hwnd, out var pid);
        if (!force && lieSlot.TryGetValue(hwnd, out var last) && last == slot) return;
        lieSlot[hwnd] = slot;
        LieSet(hwnd, slot);
        PostMessage(hwnd, WM_DISPLAYCHANGE, IntPtr.Zero, IntPtr.Zero);
        Log.Info($"lie reslot {Describe(hwnd)} -> {slot}");
    }

    // Win+Alt / Alt+T: toggle the active window's own fullscreen so its chrome shows, then hides again.
    // Reveal presses that arrive while this window's previous toggle is still running. Each used to queue a full F11 round trip behind the last
    // (ten presses took 12 seconds in the sandbox); now they collapse to their parity and run once when the window is free.
    static readonly HashSet<IntPtr> pendingToggle = [];

    static void ToggleRevealLie(IntPtr hwnd)
    {
        if (IsIconic(hwnd)) return;   // nothing to reveal on a minimized window
        if (OpActive(hwnd)) { if (!pendingToggle.Remove(hwnd)) pendingToggle.Add(hwnd); Log.Info($"reveal {Describe(hwnd)}: busy, press remembered"); return; }
        if (!managed.ContainsKey(hwnd) || notCapable.Contains(ProcessName(hwnd))) { Log.Info($"reveal: {Describe(hwnd)} not lie-managed"); return; }
        GetWindowThreadProcessId(hwnd, out var pid);
        if (lie.Remove(hwnd))
        {
            var fsNow = InAppFullscreen(hwnd); // before the slot is forgotten
            var oldSlot = lieSlot.GetValueOrDefault(hwnd); var monitor = Frames.MonitorRect(hwnd);
            revealed.Add(hwnd);
            lieSlot.Remove(hwnd);
            LieClear(hwnd); // a manual F11 now uses the real monitor
            // Wait until it has really left: the hide that follows is queued right behind this on the sender thread.
            if (fsNow) Op(hwnd, () => { MonitorLie.LeaveFullscreen(hwnd, oldSlot, monitor); });
            Log.Info($"reveal {Describe(hwnd)}: {(fsNow ? "exit fullscreen, chrome shown" : "was not fullscreen, nothing to exit")}");
        }
        else { revealed.Remove(hwnd); EnterLie(hwnd); }
    }

    // After wake / display change: re-push each fullscreen window's STORED slot (which may be a tiled half), not a
    // recomputed one, so a Win+Left/Right tile survives the wake.
    static void ReassertLie()
    {
        foreach (var hwnd in lie.ToArray())
        {
            if (!IsWindow(hwnd) || IsIconic(hwnd) || !lieSlot.TryGetValue(hwnd, out var slot)) continue;
            GetWindowThreadProcessId(hwnd, out var pid);
            LieSet(hwnd, slot);
            PostMessage(hwnd, WM_DISPLAYCHANGE, IntPtr.Zero, IntPtr.Zero);
        }
    }

    // Shutdown: synchronous (the background sender thread may not drain before the process exits), so every app
    // actually leaves fullscreen and gets its chrome back. Runs on the message-loop thread, not a hook callback.
    static void ExitAllLie()
    {
        foreach (var hwnd in lie.ToArray())
        {
            if (!IsWindow(hwnd)) continue;
            GetWindowThreadProcessId(hwnd, out var pid);
            LieClear(hwnd);
            // Only toggle windows that ARE fullscreen: a blind F11 here flipped non-fullscreen windows INTO
            // fullscreen at shutdown and left them that way for the next start to trip over.
            if (!InAppFullscreen(hwnd)) continue;
            // On another virtual desktop the window cannot be focused without dragging the user there: send it the key as messages instead.
            if (!Desk.OnCurrentDesktop(hwnd)) { Desk.PostF11(hwnd); continue; }
            MonitorLie.LeaveFullscreen(hwnd, lieSlot.GetValueOrDefault(hwnd), Frames.MonitorRect(hwnd)); // wait, don't hope
        }
        lie.Clear();
        lieSlot.Clear();
    }

    // Win+Arrow tiling. A fullscreen app can't be snapped by Windows (no resize frame) and injecting Win+Arrow while
    // the user still holds Win mis-fires (opened the Game Bar). Instead: for a lie-fullscreen window just change the
    // slot it believes is its monitor to the left/right half (or full monitor) and have it re-read - it resizes into
    // that region, still borderless, no key injection. Normal windows are positioned directly.
    static void SnapTile(IntPtr hwnd, int vk, Rect? slotOverride = null)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return;
        // Rapid Win+Arrow: the previous tile is still in flight on the sender thread, and its leave/enter decision
        // was taken from state this press is about to change. Keep only the LATEST request and apply it when the
        // in-flight op completes (WM_APP_OPDONE), from fresh state - the user wants the last one anyway, no flapping.
        if (OpActive(hwnd)) { pendingSnap[hwnd] = vk; Log.Info($"snap {Describe(hwnd)} vk=0x{vk:X}: queued behind in-flight op"); return; }
        var fullscreen = UseLie && lie.Contains(hwnd);
        if (vk == Vk.Down) // minimize
        {
            if (fullscreen)
            {
                // Leave the app's fullscreen FIRST (waited), then minimize. A window minimized while still in its own
                // fullscreen restores into an unpredictable state (normal-at-slot or fullscreen-at-monitor) and the
                // re-enter then toggles the wrong way (measured: "lie ok" on a window that was not fullscreen).
                GetWindowThreadProcessId(hwnd, out var pid0);
                var fsNow0 = InAppFullscreen(hwnd); var oldSlot0 = lieSlot.GetValueOrDefault(hwnd); var mon0 = Frames.MonitorRect(hwnd);
                lie.Remove(hwnd); lieSlot.Remove(hwnd); LieClear(hwnd);
                Op(hwnd, () =>
                {
                    if (fsNow0) MonitorLie.LeaveFullscreen(hwnd, oldSlot0, mon0);
                    ShowWindow(hwnd, SW_MINIMIZE);
                });
            }
            else Op(hwnd, () => ShowWindow(hwnd, SW_MINIMIZE));   // off the loop: a busy app made ShowWindow block it for ~85ms (sandbox: loop-stall)
            Log.Info($"snap {Describe(hwnd)} minimize");
            return;
        }
        // Fullscreen covers the whole monitor (taskbar included), so tile within the monitor bounds; a normal window
        // tiles within the work area so it doesn't cover the taskbar.
        var area = fullscreen ? Frames.MonitorRect(hwnd) : (Frames.MonitorOf(hwnd)?.Work ?? Frames.MonitorRect(hwnd));
        var slot = slotOverride ?? vk switch { Vk.Left => LieLogic.LeftHalf(area), Vk.Right => LieLogic.RightHalf(area), _ => area };
        if (fullscreen)
        {
            // A Chromium/Electron app reads its fullscreen bounds once, at F11 time, and ignores a later monitor
            // change, so re-tiling means: set the new slot, then toggle the app's fullscreen off and on (no Win-key
            // injection - that mis-fired into the Game Bar). The re-enter reads the new slot and fills the half.
            GetWindowThreadProcessId(hwnd, out var pid);
            var fsNow = InAppFullscreen(hwnd); // against the OLD slot, before it changes
            var oldSlot = lieSlot.GetValueOrDefault(hwnd);
            lieSlot[hwnd] = slot;
            LieSet(hwnd, slot);
            Log.Info($"snap {Describe(hwnd)} -> {slot} (re-fullscreen{(fsNow ? "" : ", was not fullscreen")})");
            var held = keys.Held; // Win is still physically held from the Win+Arrow chord
            Op(hwnd, () =>
            {
                // First try it in place: the slot is already set, so just move the window. No F11, no leaving fullscreen, so
                // nothing flashes (measured: leave + re-enter showed the app's title bar for ~100ms on every tile).
                // The user's own design: let the app go fullscreen honestly, then move it into its slot with SWP_NOSENDCHANGING.
                if (fsNow)   // only a window that IS in its own fullscreen; anything else must be entered properly below
                {
                    Frames.MoveUnclamped(hwnd, slot);
                    if (MonitorLie.WaitRect(hwnd, r => LieLogic.FilledSlot(r, slot), 350)) return;
                    Log.Info($"snap {Describe(hwnd)}: the in-place move did not hold, falling back to leaving and re-entering fullscreen");
                }
                Send(KeyEngine.Lift(held));              // release Win (masked) so the app sees plain F11, not Win+F11
                // State-driven, not time-driven: wait until it has actually left, then until it actually fills the
                // new slot (EnterFullscreen re-asserts foreground first: leaving bounces focus to a bystander).
                if (fsNow && !MonitorLie.LeaveFullscreen(hwnd, oldSlot, area)) Log.Info($"snap {Describe(hwnd)}: did not leave fullscreen in time");
                if (!MonitorLie.EnterFullscreen(hwnd, slot, area)) Log.Info($"snap {Describe(hwnd)}: did not fill {slot} in time");
                Send(KeyEngine.Restore(held, keys.Held));
            });
        }
        else if (vk == Vk.Up) { Op(hwnd, () => ShowWindow(hwnd, SW_MAXIMIZE)); Log.Info($"snap {Describe(hwnd)} maximize"); }
        else { Op(hwnd, () => { ShowWindow(hwnd, SW_RESTORE); Frames.PlaceVisible(hwnd, slot); }); Log.Info($"snap {Describe(hwnd)} -> {slot}"); }
    }

    // Alt+M. A fullscreen window has no maximize button, so it means "fill the monitor", and pressing it again goes back to the
    // tile it was in. (The old behaviour posted a maximize to the app, which left the manager's slot stale: measured by the sandbox.)
    static readonly Dictionary<IntPtr, Rect> preMaxSlot = [];

    static void ToggleMaximizeActive(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return;
        if (UseLie && lie.Contains(hwnd) && lieSlot.TryGetValue(hwnd, out var slot) && !OpActive(hwnd))
        {
            var monitor = Frames.MonitorRect(hwnd);
            if (LieLogic.IsAppFullscreen(slot, monitor, 2))
            {
                if (preMaxSlot.Remove(hwnd, out var back)) SnapTile(hwnd, Vk.Left, back);
                return;   // already the whole monitor and nothing to go back to
            }
            preMaxSlot[hwnd] = slot;
            SnapTile(hwnd, Vk.Up);
            return;
        }
        var cmd = Chrome.WindowCommand(Act.ToggleMaximize, IsZoomed(hwnd));
        Log.Info($"syscommand 0x{cmd:X} -> {Describe(hwnd)}");
        if (!PostMessage(hwnd, WM_SYSCOMMAND, (IntPtr)cmd, IntPtr.Zero)) Log.Win32("PostMessage(WM_SYSCOMMAND)");
    }

    // Snap Layouts (Win+Z) is interactive - the user picks a zone from Windows' flyout - so we can't compute it.
    // A fullscreen app can't be snapped, so: drop it out of fullscreen, show the flyout; when Windows snaps it into
    // the chosen zone (a move to a non-maximized rect) we re-enter fullscreen into that zone. A cancel (no pick)
    // restores fullscreen via the timer.
    static void OpenLayouts(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        var held = keys.Held;
        if (UseLie && lie.Remove(hwnd))
        {
            var fsNow = InAppFullscreen(hwnd); // before the slot is forgotten; F11 toggles
            var oldSlot = lieSlot.GetValueOrDefault(hwnd); var monitor = Frames.MonitorRect(hwnd);
            lieSlot.Remove(hwnd);
            GetWindowThreadProcessId(hwnd, out var pid);
            LieClear(hwnd);
            awaitingZone[hwnd] = Environment.TickCount64 + ZoneWaitMs;
            StartTimer(TimerZone, 250);
            Log.Info($"snap layouts {Describe(hwnd)}: exit fullscreen, show picker, re-enter on pick");
            Op(IntPtr.Zero, () => // no foreground re-assert: it would dismiss the Snap Layouts flyout
            {
                Send(KeyEngine.Lift(held));
                if (!MonitorLie.Focus(hwnd)) return;   // the user moved on: the picker would open over the wrong desktop
                if (fsNow) MonitorLie.LeaveFullscreen(hwnd, oldSlot, monitor); // only if it IS fullscreen, and wait until it has left
                ShowWindow(hwnd, SW_MAXIMIZE); Thread.Sleep(150); // force a clean baseline: only a real zone pick is smaller
                Send(KeyEngine.Tap(Vk.LWin, 'Z'));          // open the Snap Layouts flyout
                Send(KeyEngine.Restore(held, keys.Held));
            });
        }
        else Inject(() => { Send(KeyEngine.Lift(held)); Send(KeyEngine.Tap(Vk.LWin, 'Z')); Send(KeyEngine.Restore(held, keys.Held)); });
    }

    // A window waiting for a Snap Layouts pick landed somewhere (or we gave up): re-enter fullscreen into whatever
    // rect it now occupies (the chosen zone, or the full monitor if the pick was cancelled).
    static void ResolveZone(IntPtr hwnd)
    {
        awaitingZone.Remove(hwnd);
        if (IsWindow(hwnd) && !IsIconic(hwnd) && managed.ContainsKey(hwnd) && !notCapable.Contains(ProcessName(hwnd))) EnterLie(hwnd);
    }

    static void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (Desk.Sandbox) CountRealEvent(evt);
        var sw = Stopwatch.StartNew();
        OnWinEventCore(evt, hwnd, idObject, idChild);
        Slow(sw, () => $"win event 0x{evt:X} on 0x{hwnd:X}");
    }

    static void OnWinEventCore(uint evt, IntPtr hwnd, int idObject, int idChild)
    {
        if (idObject != OBJID_WINDOW || idChild != 0 || hwnd == IntPtr.Zero) return;
        try
        {
            switch (evt)
            {
                case EVENT_SYSTEM_FOREGROUND:
                    // A managed window the user activated: put it into its slot - but only once it has STAYED
                    // foreground for ActivateSettleMs (see pendingFg), so a focus bounce from our own toggles can't
                    // trigger a probe on a bystander. (Deferred entry: nothing is fullscreened at startup.)
                    if (hints.Active) HideHints(); // the badges were laid out for the previous foreground
                    if (prefetchOn) StartTimer(TimerPrefetch, PrefetchSettleMs); // work the labels out for the new window before they are asked for
                    if (UseLie && managed.ContainsKey(hwnd)) { pendingFg = hwnd; StartTimer(TimerActivate, ActivateSettleMs); }
                    break;
                case EVENT_OBJECT_SHOW: Manage(hwnd); break;
                case EVENT_OBJECT_DESTROY:
                    userShown.Remove(hwnd); coverAttempts.Remove(hwnd); crops.Remove(hwnd); lock (names) names.Remove(hwnd); backdrops.Remove(hwnd); revealed.Remove(hwnd); layout.Remove(hwnd);
                    lie.Remove(hwnd); lieSlot.Remove(hwnd); awaitingZone.Remove(hwnd); heals.Remove(hwnd); preMaxSlot.Remove(hwnd); minimizedLie.Remove(hwnd); LieClear(hwnd);
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
        // While displays change (or right after a wake) Windows moves windows around itself: don't crop, cover or
        // record any of that. Checking the monitor set here catches the change even before the timer poll does.
        if (!settling && Layout.Signature(Displays()) != stableDisplays) BeginSettle("displays changed");
        if (settling) { movedWindows.Clear(); return; }

        foreach (var hwnd in movedWindows.ToArray())
        {
            movedWindows.Remove(hwnd);
            // The focused window finished moving or resizing (its own fullscreen transition, a tile): a scan taken before this describes the old
            // rectangles and would be thrown away, so take a fresh one once it has settled.
            if (prefetchOn && hwnd == Desk.Foreground()) StartTimer(TimerPrefetch, 300);
            if (!managed.ContainsKey(hwnd) || !IsWindow(hwnd)) continue;
            if (UseLie)
            {
                // Waiting for a Snap Layouts pick: when Windows has snapped it into a zone (no longer maximized, and
                // smaller than the monitor), re-enter fullscreen into that zone.
                if (awaitingZone.ContainsKey(hwnd) && Volatile.Read(ref opsInFlight) == 0 && !IsZoomed(hwnd) && Frames.FrameBounds(hwnd) is { } f && f != Frames.MonitorRect(hwnd))
                    ResolveZone(hwnd);
                // Otherwise the slot is set explicitly (enter, snap, monitor-move), never recomputed from the window's
                // own rect here - a fullscreen app reports IsZoomed=true, so recomputing would always snap it back to
                // the whole monitor and undo a Win+Left/Right tile. Just keep the layout record for sleep/wake.
                var swMoved = Stopwatch.StartNew();
                RecordLayout(hwnd);
                var tLayout = swMoved.ElapsedMilliseconds;
                if (FollowMonitor(hwnd)) continue;   // the user moved it to another monitor: the slot goes with it, the app re-reads
                ReconcileLie(hwnd);
                if (swMoved.ElapsedMilliseconds >= 40) Log.Info($"SLOW moved-window handling for {Describe(hwnd)}: layout {tLayout}ms, reconcile {swMoved.ElapsedMilliseconds - tLayout}ms");
                continue;

            }
            if (Chrome.NeedsRestrip(Frames.Style(hwnd), userShown.Contains(hwnd)))
            {
                Log.Info($"restrip {Describe(hwnd)} (app restored its caption)");
                Frames.Strip(hwnd);
            }
            if (crops.ContainsKey(hwnd)) ApplyCrop(hwnd);
            else CoverIfMaximized(hwnd);
            RecordLayout(hwnd);
        }
    }

    // The user moved a fullscreen window to another monitor (Win+Shift+Arrow, a drag, a taskbar move): the slot follows it, the old
    // monitor's entry is released, and the app is told to re-read so it fills the new slot in place. Our own ops are excluded.
    static bool FollowMonitor(IntPtr hwnd)
    {
        if (!lie.Contains(hwnd) || settling || probing.Contains(hwnd) || OpActive(hwnd) || awaitingZone.ContainsKey(hwnd) || IsIconic(hwnd)) return false;
        if (!lieEntry.TryGetValue(hwnd, out var e) || !lieSlot.TryGetValue(hwnd, out var slot)) return false;
        var now = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (now == IntPtr.Zero || now == e.Mon) return false;
        var from = Frames.MonitorRectOf(e.Mon); var to = Frames.MonitorRectOf(now);
        if (from.Width <= 0 || to.Width <= 0) return false;
        var mapped = LieLogic.FollowMonitor(slot, from, to);
        lieSlot[hwnd] = mapped;
        heals.Remove(hwnd);
        LieSet(hwnd, mapped);
        PostMessage(hwnd, WM_DISPLAYCHANGE, IntPtr.Zero, IntPtr.Zero);
        Log.Info($"lie follow {Describe(hwnd)}: moved to another monitor, slot {slot} -> {mapped}");
        return true;
    }

    // What the manager believes about a fullscreen window must be what the window is. Something else can change a window behind our back:
    // the user presses the app's own F11, the app minimizes itself, or another program moves or resizes it. Believing the old state
    // is how a window ends up toggled the wrong way, so each case is reconciled (measured in the sandbox: appf11 and ext-* left the
    // manager and the app disagreeing). Bounded: a window that keeps being moved is healed three times, not forever.
    static readonly Dictionary<IntPtr, int> heals = [];
    static readonly HashSet<IntPtr> minimizedLie = [];   // fullscreen windows minimized from outside, waiting to come back

    static void ReconcileLie(IntPtr hwnd)
    {
        if (settling || probing.Contains(hwnd) || OpActive(hwnd) || awaitingZone.ContainsKey(hwnd) || !IsWindow(hwnd) || !GetWindowRect(hwnd, out var r)) return;
        if (lie.Contains(hwnd))
        {
            // Minimized from outside (a taskbar click): the app is still in its own fullscreen, so what we believe stays true. Remember it,
            // so that if it comes back WITHOUT its fullscreen we re-enter rather than treat it as the user revealing the chrome.
            if (IsIconic(hwnd)) { minimizedLie.Add(hwnd); return; }
            var restored = minimizedLie.Remove(hwnd);
            if (!lieSlot.TryGetValue(hwnd, out var slot)) return;
            if (LieLogic.FilledSlot(r, slot)) { heals.Remove(hwnd); return; }
            if (Chrome.HasCaption(Frames.Style(hwnd)))
            {
                if (restored) { DropLie(hwnd, "it came back from minimize without its fullscreen", reveal: false); MaybeEnterLie(hwnd); }
                else DropLie(hwnd, "the app left its own fullscreen (its title bar is back)", reveal: true);
                return;
            }
            var n = heals[hwnd] = heals.GetValueOrDefault(hwnd) + 1;   // still borderless but not where we put it: something moved it
            if (n <= 3) { Log.Info($"lie heal {Describe(hwnd)}: {r} -> {slot}"); Frames.MoveUnclamped(hwnd, slot); }
            else Log.Info($"lie heal {Describe(hwnd)}: giving up after {n - 1} attempts, it keeps moving");
            return;
        }
    }

    static void DropLie(IntPtr hwnd, string why, bool reveal)
    {
        lie.Remove(hwnd); lieSlot.Remove(hwnd); heals.Remove(hwnd);
        GetWindowThreadProcessId(hwnd, out var pid);
        LieClear(hwnd);
        if (reveal) revealed.Add(hwnd);
        Log.Info($"lie dropped {Describe(hwnd)}: {why}");
    }

    static void CoverIfMaximized(IntPtr hwnd)
    {
        if (settling || userShown.Contains(hwnd) || !GetWindowRect(hwnd, out var window)) return;
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
        if (settling || revealed.Contains(hwnd) || userShown.Contains(hwnd) || IsIconic(hwnd) || !crops.TryGetValue(hwnd, out var tracker)) return;
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
        if (UseLie) { ToggleRevealLie(hwnd); return; }
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
    static void Highlight(IntPtr hwnd, uint ms = HighlightMs)
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
        StartTimer(TimerHighlight, ms);
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
    static void ShowHelp() => Log.Info(HelpWindow.Toggle(Desk.Foreground()) ? "hotkey list opened" : "hotkey list closed");

    // ---- sleep and display changes ----

    static List<Display> Displays() => [.. Frames.AllMonitors().Select(m => new Display(m.Device, m.Bounds))];

    // Window placement rects are in workspace coordinates: relative to the primary monitor's work area.
    static (int X, int Y) WorkspaceOrigin()
    {
        var w = Frames.AllMonitors().FirstOrDefault(m => m.Primary)?.Work ?? default;
        return (w.Left, w.Top);
    }

    // Every 700ms (hover timer): a wall-clock jump means the machine woke up; a changed monitor set means displays
    // are being added/removed/rearranged (also what display sleep does to DisplayPort monitors).
    static void CheckDisplays()
    {
        var now = Environment.TickCount64;
        if (Layout.SleptBetween(lastTick, now)) BeginSettle($"woke up ({(now - lastTick) / 1000}s without a tick)");
        lastTick = now;
        var sig = Layout.Signature(Displays());
        if (sig != pendingDisplays) { pendingDisplays = sig; BeginSettle("displays changed"); }
    }

    static void OnPower(int evt)
    {
        if (evt == PBT_APMSUSPEND)
        {
            settling = true; // freeze: the layout recorded now is what gets restored on wake
            KillTimer(msgWnd, TimerSettle);
            Log.Info("suspend: window tracking frozen");
        }
        else if (evt is PBT_APMRESUMEAUTOMATIC or PBT_APMRESUMESUSPEND)
        {
            Log.Info($"resume (power event 0x{evt:X})");
            BeginSettle("resume");
        }
    }

    // Stop reacting to window moves and (re)start the settle timer; it fires once displays stay unchanged for
    // SettleMs, then the windows are put back and re-cropped.
    static void BeginSettle(string why)
    {
        if (!settling) Log.Info($"{why}: window tracking frozen until displays settle");
        else Log.Debug($"{why}: settle timer restarted");
        settling = true;
        movedWindows.Clear();
        StartTimer(TimerSettle, Layout.SettleMs);
    }

    // The rect Windows laid the window out at, never our stretched crop rect. Cropped + maximized: its slot.
    // Cropped + floating/snapped: what is visible (the slot is already widened by the invisible borders, and
    // re-cropping from it would widen it twice: measured +20x10px after a simulated wake).
    static Rect LaidOutRect(IntPtr hwnd, Rect rect)
    {
        if (!crops.TryGetValue(hwnd, out var t) || t.SlotFor(rect) is not { } slot) return rect;
        if (IsZoomed(hwnd) || GetWindowRgnBox(hwnd, out var region) == 0) return slot;
        return Crop.VisibleOnScreen(rect, region);
    }

    static void RecordLayout(IntPtr hwnd)
    {
        if (settling || IsIconic(hwnd) || !GetWindowRect(hwnd, out var rect) || Frames.MonitorOf(hwnd) is not { } mon) return;
        var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!GetWindowPlacement(hwnd, ref wp)) return;
        var (ox, oy) = WorkspaceOrigin();
        layout[hwnd] = new LayoutRecord(new Display(mon.Device, mon.Bounds), LaidOutRect(hwnd, rect), IsZoomed(hwnd), wp.rcNormal.Offset(ox, oy));
    }

    // Phase 1, displays settled: drop the pre-sleep clips and crop state, and put windows back where they were.
    static void RepairMoveBack()
    {
        var displays = Displays();
        stableDisplays = pendingDisplays = Layout.Signature(displays);
        int moved = 0, reset = 0;
        foreach (var hwnd in managed.Keys.ToArray())
        {
            if (!IsWindow(hwnd) || IsIconic(hwnd) || !GetWindowRect(hwnd, out var rect)) continue;
            var slot = LaidOutRect(hwnd, rect); // before the clip is dropped: it is part of the answer
            if (crops.ContainsKey(hwnd))
            {
                // The clip and crop history describe the geometry from before: start the crop over from Windows' rect.
                Frames.SetRegion(hwnd, null);
                crops[hwnd] = new CropTracker();
                cropRetries.Remove(hwnd);
                reset++;
            }
            coverAttempts.Remove(hwnd);

            var target = Layout.RestoreTarget(layout.GetValueOrDefault(hwnd), displays);
            var device = Frames.MonitorOf(hwnd)?.Device ?? "";
            if (target is not null && !Layout.AlreadyThere(target, device, IsZoomed(hwnd), slot))
            {
                PlaceBack(hwnd, target);
                moved++;
            }
            else if (slot != rect) Frames.MoveUnclamped(hwnd, slot); // still stretched: back onto its slot first
        }
        Log.Info($"displays settled ({displays.Count} monitors): {moved} windows put back, {reset} crops reset");
    }

    static void PlaceBack(IntPtr hwnd, LayoutRecord target)
    {
        var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!GetWindowPlacement(hwnd, ref wp)) { Log.Win32("GetWindowPlacement"); return; }
        var (ox, oy) = WorkspaceOrigin();
        // Like MoveToMonitor: place it normal first (Windows ignores a new restore rect on a maximized window), then
        // set the rect again once it is on that monitor (DPI rescale), then maximize if it was.
        var normal = target.Zoomed ? target.Normal : target.Slot;
        wp.rcNormal = normal.Offset(-ox, -oy);
        wp.showCmd = SW_SHOWNORMAL;
        wp.flags = 0;
        if (!SetWindowPlacement(hwnd, ref wp)) { Log.Win32("SetWindowPlacement"); return; }
        SetWindowPos(hwnd, IntPtr.Zero, normal.Left, normal.Top, normal.Width, normal.Height, SWP_NOZORDER | SWP_NOACTIVATE);
        if (target.Zoomed) ShowWindow(hwnd, SW_MAXIMIZE);
        Log.Info($"put back {Describe(hwnd)} on {target.Display.Device}{(target.Zoomed ? " maximized" : $" at {target.Slot}")}");
    }

    // Phase 2: windows have landed; resume tracking and re-apply what the sleep may have lost (DWM border/corner
    // attributes after a DWM restart, crops, the taskbar cover).
    static void RepairRecrop()
    {
        settling = false;
        if (UseLie)
        {
            ReassertLie();
            foreach (var hwnd in managed.Keys.ToArray()) if (IsWindow(hwnd) && !IsIconic(hwnd)) RecordLayout(hwnd);
            Log.Info("lie slots re-asserted, tracking resumed");
            CursorTheme.EnsureApplied("after wake/display change");
            return;
        }
        foreach (var hwnd in managed.Keys.ToArray())
        {
            if (!IsWindow(hwnd) || IsIconic(hwnd)) continue;
            if (!userShown.Contains(hwnd)) Frames.Strip(hwnd);
            if (crops.ContainsKey(hwnd)) ApplyCrop(hwnd);
            else CoverIfMaximized(hwnd);
            RecordLayout(hwnd);
        }
        Log.Info("windows re-cropped, tracking resumed");
        CursorTheme.EnsureApplied("after wake/display change");
    }

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
        if (UseLie) UpdateLieSlot(hwnd, force: true); // fullscreen window: re-read the new monitor's slot
    }

    // ---- shared ----

    static void ToggleChrome(IntPtr hwnd)
    {
        if (UseLie) { ToggleRevealLie(hwnd); return; }
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
        if (UseLie)
        {
            ExitAllLie();
            Log.Info($"restored {managed.Count} windows (exited fullscreen)");
            return;
        }
        foreach (var hwnd in crops.Keys) if (IsWindow(hwnd)) Uncrop(hwnd);
        foreach (var (hwnd, original) in managed)
        {
            if (!IsWindow(hwnd)) continue;
            Frames.Unstrip(hwnd, original);
            if (IsZoomed(hwnd) && !crops.ContainsKey(hwnd)) { ShowWindow(hwnd, SW_RESTORE); ShowWindow(hwnd, SW_MAXIMIZE); } // back onto the work area
        }
        Log.Info($"restored {managed.Count} windows ({crops.Count} uncropped)");
    }

    // Called from the loop AND from worker threads (log lines inside ops), so the cache is locked.
    static string ProcessName(IntPtr hwnd)
    {
        lock (names) if (names.TryGetValue(hwnd, out var cached)) return cached;
        GetWindowThreadProcessId(hwnd, out var pid);
        string n;
        try { n = Process.GetProcessById((int)pid).ProcessName; } catch (ArgumentException) { n = "?"; }
        lock (names) return names[hwnd] = n;
    }

    // Standalone check of the MonitorLie engine against one window; does not touch the managing flow.
    // Usage: WindowManager.exe --lie-test <hwnd> <L> <T> <R> <B>
    static void LieTest(string[] args)
    {
        var i = Array.IndexOf(args, "--lie-test");
        var hwnd = (IntPtr)long.Parse(args[i + 1]);
        var slot = new Rect(int.Parse(args[i + 2]), int.Parse(args[i + 3]), int.Parse(args[i + 4]), int.Parse(args[i + 5]));
        GetWindowThreadProcessId(hwnd, out var pid);
        var result = Path.Combine(Path.GetTempPath(), "lie_test_result.txt");
        if (!MonitorLie.Install()) { File.WriteAllText(result, "install failed\n"); return; }
        LieSet(hwnd, slot);
        for (var k = 0; k < 12; k++) { PostMessage(hwnd, 0, IntPtr.Zero, IntPtr.Zero); Thread.Sleep(30); } // map the DLL in
        Thread.Sleep(400);
        GetWindowRect(hwnd, out var pre);
        var ok = MonitorLie.TryFullscreen(hwnd, slot, Frames.MonitorRect(hwnd)) is { } after && LieLogic.FilledSlot(after, slot);
        GetWindowRect(hwnd, out var fs);
        var line = $"pid={pid} PRE={pre} FS={fs} slot={slot} filled={ok}";
        File.WriteAllText(result, line + "\n");
        Log.Info($"lie-test {line}");
        // restore the window and tear down
        Desk.SetForeground(hwnd); Thread.Sleep(300); Desk.SendF11(hwnd); Thread.Sleep(600);
        LieClear(hwnd);
        ShowWindow(hwnd, SW_RESTORE); Thread.Sleep(250); ShowWindow(hwnd, SW_MAXIMIZE); Thread.Sleep(400);
        MonitorLie.Uninstall();
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
            var selfDrawn = Crop.ShouldCrop(Crop.IsSelfDrawn(r, Frames.ClientOnScreen(hwnd), dpi), cls); // what would be cropped
            lines.Add($"{Describe(hwnd),-32} manageable={Chrome.IsManageable(style, ex, cls, ProcessName(hwnd)),-5} caption={Chrome.HasCaption(style),-5} " +
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
            Desk.SetCursor(altDownPos.X, altDownPos.Y);
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

    static void SendMouse(params INPUT[] inputs) => Desk.SendMouse(inputs);

    static void Send(KeyStroke[] strokes) => Desk.SendKeys(strokes);
}
