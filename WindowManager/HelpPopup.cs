using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace WindowManager;

/// <summary>
/// The hotkey list (both Alt keys) as a WebView2 popup, drawn like the table editor: the same app.css, Plus Jakarta Sans, light theme, cards with the
/// hard shadow, the window clipped to the cards the page reports (web/hotkeys.html, web/js/hotkeys.js). Same shape as <see cref="TablePopup"/>: one STA
/// thread owns a raw Win32 popup with a WebView2 controller; the page is created on the first open (about 0.6 s), then kept hidden for reuse. The
/// public API is thread-safe. The two hooks below are how the updater (Updates.cs) puts a line and a button into the menu without this file knowing it.
/// </summary>
public static class HelpPopup
{
    public const string Host = "wm.hotkeys";
    /// <summary>The page's layout viewport, CSS px. A monitor too small for it gets a smaller render scale (the layout stays the same), not a scrollbar.</summary>
    public const double LogicalWidth = 1100, LogicalHeight = 760;

    /// <summary>The updater's say, read each time the menu opens: null = nothing to show. CanApply puts an "Update" button next to the text.</summary>
    public static Func<(string Text, bool CanApply)?>? UpdateLine { get; set; }
    /// <summary>Called (on a pool thread, after the menu closed) when the Update button is clicked.</summary>
    public static Action? UpdateClicked { get; set; }

    // ---- test switches and measurements ----
    /// <summary>Test only: never activate or focus, show with SW_SHOWNOACTIVATE and force x = -32000.</summary>
    public static bool TestOffScreen { get; set; }
    /// <summary>Test only: a user-data folder other than %LOCALAPPDATA%\WindowManager\WebView2 (read by the next Prewarm).</summary>
    public static string? UserDataFolderOverride { get; set; }
    public static int IdleSuspendMs { get; set; } = 30_000;
    public static double LastOpenToShownMs { get; private set; }
    public static double LastScale { get; private set; }
    public static IntPtr WindowHandle => hwnd;
    public static bool IsReady => ready;
    /// <summary>True from the moment the window is shown until it is hidden.</summary>
    public static bool IsShown => shown;
    /// <summary>True from Toggle (also while the page is still starting) until the menu is hidden.</summary>
    public static bool IsOpen => Volatile.Read(ref open) == 1;

    // ---- shared state (any thread) ----
    static readonly object startLock = new();
    static Thread? thread;
    static int open;
    static volatile bool ready, broken, shown, suspended;
    static IntPtr hwnd;                                   // written by the popup thread before windowUp is set
    static ManualResetEventSlim windowUp = new();
    static readonly ManualResetEventSlim closed = new(true);   // set while the menu is not open
    static readonly System.Collections.Concurrent.ConcurrentQueue<Action> work = new();

    // ================= pure helpers =================

    /// <summary>The message the page renders: the version, the updater's line (or null) and the hotkeys grouped as given, each group in the order of its first row.</summary>
    public static string BuildOpenMessage(string version, (string Text, bool CanApply)? update, IEnumerable<(string Category, string Keys, string What)> help) =>
        JsonSerializer.Serialize(new
        {
            type = "open",
            version,
            update = update is { } u ? new { text = u.Text, canApply = u.CanApply } : null,
            groups = help.GroupBy(h => h.Category).Select(g => new { name = g.Key, rows = g.Select(h => new { keys = h.Keys, what = h.What }).ToList() }).ToList(),
        });

    /// <summary>The message for this run: this build's version, the updater's line as it stands, KeyEngine's hotkeys.</summary>
    public static string BuildOpenMessage()
    {
        (string, bool)? line = null;
        try { line = UpdateLine?.Invoke(); } catch (Exception e) { Log.Info("hotkey menu: UpdateLine failed: " + e.Message); }
        return BuildOpenMessage(AppVersion.Display, line, KeyEngine.SortedHelp());
    }

    /// <summary>A page message: TablePopup's ready / shape / close, plus "update" (the Update button). Null when it is not one of ours.</summary>
    public static PageMessage? ParseMessage(string json)
    {
        var m = TablePopup.ParseMessage(json);
        if (m is not null) return m;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("type", out var t) && t.GetString() == "update")
                return new PageMessage("update", [], 0, 0, "", "", false);
        }
        catch (JsonException) { }
        return null;
    }

    /// <summary>The render scale: the monitor's DPI scale, lowered so the page's whole viewport fits the work area (with a margin).</summary>
    public static double Scale(double dpiScale, Size work) =>
        Math.Min(dpiScale, Math.Min(work.Width * 0.96 / LogicalWidth, work.Height * 0.96 / LogicalHeight));

    // ================= public API =================

    /// <summary>Starts the popup thread: window, WebView2, the page. Returns at once; IsReady turns true when the page said "ready".</summary>
    public static void Prewarm(string? webFolder = null)
    {
        lock (startLock)
        {
            if (broken) ShutdownLocked();
            if (thread is not null) return;
            ready = false;
            windowUp = new ManualResetEventSlim();
            thread = new Thread(() => ThreadMain(webFolder ?? Path.Combine(AppContext.BaseDirectory, "web"))) { IsBackground = true, Name = "HelpPopup" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
        windowUp.Wait(5000);
    }

    /// <summary>Both Alts: opens the menu centred on the anchor window's monitor (the primary one when anchor is zero), or closes it when it is open. True if it opened. Never blocks on the page.</summary>
    public static bool Toggle(IntPtr anchor)
    {
        if (Interlocked.Exchange(ref open, 1) == 1)
        {
            Post(() => pt.Close("toggle"));
            return false;
        }
        closed.Reset();
        try
        {
            var message = BuildOpenMessage();
            Prewarm();
            Post(() => pt.BeginOpen(anchor, message, Stopwatch.GetTimestamp()));
            return true;
        }
        catch (Exception e)
        {
            Log.Info($"hotkey menu: open failed: {e}");
            Interlocked.Exchange(ref open, 0); closed.Set();
            return false;
        }
    }

    /// <summary>--hotkeys: shows the menu on the primary monitor and returns when it is closed (no hooks are running).</summary>
    public static void ShowAndWait()
    {
        if (Toggle(IntPtr.Zero)) closed.Wait();
        Shutdown();
    }

    /// <summary>Closes the controller and the window and ends the thread.</summary>
    public static void Shutdown()
    {
        lock (startLock) ShutdownLocked();
    }

    static void ShutdownLocked()
    {
        var t = thread;
        thread = null;
        if (t is null) { broken = false; return; }
        Post(() => pt.Teardown());
        if (!t.Join(5000)) Log.Info("hotkey menu: thread did not end in 5 s");
        ready = false; suspended = false; broken = false;
        Interlocked.Exchange(ref open, 0); closed.Set();
    }

    /// <summary>Test only: runs a script in the page and returns its JSON result.</summary>
    public static string? EvalForTest(string js, int timeoutMs = 2000)
    {
        string? result = null;
        using var done = new ManualResetEventSlim();
        Post(async () => { try { result = await pt.Eval(js); } catch (Exception e) { result = "error: " + e.Message; } finally { done.Set(); } });
        done.Wait(timeoutMs);
        return result;
    }

    /// <summary>Test only: saves a PNG of what the page shows.</summary>
    public static Task CaptureForTest(string path)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(async () =>
        {
            try { await pt.Capture(path); tcs.TrySetResult(); }
            catch (Exception e) { tcs.TrySetException(e); }
        });
        return tcs.Task;
    }

    /// <summary>Test only: what the host does when the popup is deactivated while open.</summary>
    public static void TestDeactivate() => Post(() => pt.Close("deactivated"));

    static void Post(Action a)
    {
        work.Enqueue(a);
        if (hwnd != IntPtr.Zero) Native.PostMessage(hwnd, WM_RUN, IntPtr.Zero, IntPtr.Zero);
    }

    // ================= the popup thread =================

    const uint WM_RUN = 0x8000 + 2, WM_ACTIVATE = 0x0006, WM_ACTIVATEAPP = 0x001C, WM_DPICHANGED = 0x02E0, WM_TIMER = 0x0113, WM_CLOSE = 0x0010, WM_DESTROY = 0x0002;
    const uint WS_POPUP = 0x80000000, WS_EX_TOOLWINDOW = 0x80, WS_EX_TOPMOST = 0x8;
    const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOZORDER = 4, SWP_NOACTIVATE = 0x10;
    const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4, SW_SHOW = 5;
    static readonly IntPtr HWND_TOPMOST = new(-1), DPI_PMV2 = new(-4);
    const uint T_SHAPE = 1, T_IDLE = 3;
    const int ShapeTimeoutMs = 5000;

    static readonly Native.WndProc wndProc = WndProc;          // kept alive for the window class
    static readonly Popup pt = new();                          // touched only on the popup thread

    static void ThreadMain(string webFolder)
    {
        try
        {
            TablePopup.W.SetThreadDpiAwarenessContext(DPI_PMV2); // per-monitor-v2 even in a host process that is not (the test runner)
            pt.CreateWindow();
            SynchronizationContext.SetSynchronizationContext(new PopupContext());
            windowUp.Set();
            _ = pt.InitAsync(webFolder);
            while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                Native.TranslateMessage(ref msg);
                Native.DispatchMessage(ref msg);
            }
        }
        catch (Exception e) { Log.Info($"hotkey menu: thread failed: {e}"); broken = true; pt.Failed(); }
        finally { windowUp.Set(); hwnd = IntPtr.Zero; }
    }

    static IntPtr WndProc(IntPtr h, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_RUN:
                while (work.TryDequeue(out var a))
                {
                    try { a(); } catch (Exception e) { Log.Info($"hotkey menu: work item failed: {e}"); }
                }
                return IntPtr.Zero;
            case WM_ACTIVATE:
                pt.OnActivate((int)((long)wParam & 0xFFFF), lParam);
                break;
            case WM_ACTIVATEAPP:
                if (wParam == IntPtr.Zero) pt.OnDeactivated();
                break;
            case WM_DPICHANGED:
                return IntPtr.Zero; // sized by us, from the monitor's scale
            case WM_TIMER:
                pt.OnTimer((uint)(long)wParam);
                return IntPtr.Zero;
            case WM_CLOSE:
                pt.Close("wm_close"); // Alt+F4
                return IntPtr.Zero;
            case WM_DESTROY:
                Native.PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return Native.DefWindowProc(h, msg, wParam, lParam);
    }

    /// <summary>Marshals Task continuations (the WebView2 async API) back onto the popup thread's message loop.</summary>
    sealed class PopupContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => HelpPopup.Post(() => d(state));
        public override void Send(SendOrPostCallback d, object? state)
        {
            using var done = new ManualResetEventSlim();
            HelpPopup.Post(() => { try { d(state); } finally { done.Set(); } });
            done.Wait();
        }
    }

    sealed class OpenState(IntPtr anchor, string message, long t0)
    {
        public readonly IntPtr Anchor = anchor;
        public readonly string Message = message;
        public readonly long T0 = t0;
        public double Scale = 1;
        public Size SizePx;
        public Rectangle Work;
        public bool Shown, HasBeenActive, Showing;
    }

    sealed class Popup
    {
        CoreWebView2Controller? controller;
        CoreWebView2? core;
        OpenState? cur;
        OpenState? pending;   // an open asked for while the page was still starting
        TaskCompletionSource readyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void CreateWindow()
        {
            var wc = new Native.WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEX>(),
                lpfnWndProc = wndProc,
                hInstance = Native.GetModuleHandle(null),
                hbrBackground = new IntPtr(6),
                lpszClassName = "WmHelpPopup",
            };
            if (Native.RegisterClassEx(ref wc) == 0 && Marshal.GetLastWin32Error() != 1410) // 1410 = class already registered (after Shutdown)
                throw new InvalidOperationException("RegisterClassEx failed: " + Marshal.GetLastWin32Error());
            hwnd = Native.CreateWindowEx(WS_EX_TOOLWINDOW | WS_EX_TOPMOST, wc.lpszClassName, "WindowManager hotkeys", WS_POPUP,
                -32000, -32000, (int)LogicalWidth, (int)LogicalHeight, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
            if (hwnd == IntPtr.Zero) throw new InvalidOperationException("CreateWindowEx failed: " + Marshal.GetLastWin32Error());
            uint off = 1; Native.DwmSetWindowAttribute(hwnd, 3 /* DWMWA_TRANSITIONS_FORCEDISABLED */, ref off, sizeof(uint));
        }

        // ---- startup ----
        public async Task InitAsync(string webFolder)
        {
            var sw = Stopwatch.StartNew();
            readyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);   // a fresh start after Shutdown
            try
            {
                if (!File.Exists(Path.Combine(webFolder, "hotkeys.html"))) throw new FileNotFoundException("no hotkeys.html in " + webFolder);
                if (Environment.GetEnvironmentVariable("WEBVIEW2_DEFAULT_BACKGROUND_COLOR") is null)
                    Environment.SetEnvironmentVariable("WEBVIEW2_DEFAULT_BACKGROUND_COLOR", "00FFFFFF");
                var data = UserDataFolderOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowManager", "WebView2");
                Directory.CreateDirectory(data);
                var env = await CoreWebView2Environment.CreateAsync(null, data);   // the same folder as the table editor: one browser process
                controller = await env.CreateCoreWebView2ControllerAsync(hwnd);
                core = controller.CoreWebView2;
                controller.ShouldDetectMonitorScaleChanges = false;
                controller.DefaultBackgroundColor = Color.Transparent;
                var s = core.Settings;
                s.AreDefaultContextMenusEnabled = false;
                s.AreDevToolsEnabled = false;
                s.IsStatusBarEnabled = false;
                s.IsZoomControlEnabled = false;
                s.IsPinchZoomEnabled = false;
                s.IsSwipeNavigationEnabled = false;
                s.IsBuiltInErrorPageEnabled = false;
                s.IsPasswordAutosaveEnabled = false;
                s.IsGeneralAutofillEnabled = false;
                s.AreBrowserAcceleratorKeysEnabled = false;
                core.SetVirtualHostNameToFolderMapping(Host, webFolder, CoreWebView2HostResourceAccessKind.Allow);
                core.NavigationStarting += (_, e) => { if (!e.Uri.StartsWith($"https://{Host}/", StringComparison.OrdinalIgnoreCase)) e.Cancel = true; };
                core.NewWindowRequested += (_, e) => e.Handled = true;
                core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
                core.WebMessageReceived += (_, e) => OnPageMessage(e.WebMessageAsJson);
                core.ProcessFailed += (_, e) => OnProcessFailed(e.ProcessFailedKind.ToString());
                SizeWindow(1.0, new Size((int)LogicalWidth, (int)LogicalHeight));
                controller.IsVisible = true; // loads at normal priority; hidden again once the page said ready
                core.Navigate($"https://{Host}/hotkeys.html");
                await readyTcs.Task;
                controller.IsVisible = false;
                ready = true;
                Log.Info($"hotkey menu: ready in {sw.ElapsedMilliseconds} ms");
                if (pending is { } p) { pending = null; BeginOpen(p.Anchor, p.Message, p.T0); }
                else Native.SetTimer(hwnd, T_IDLE, (uint)IdleSuspendMs, IntPtr.Zero);
            }
            catch (Exception e)
            {
                Log.Info($"hotkey menu: init failed: {e}");
                broken = true;
                Failed();
            }
        }

        /// <summary>The page cannot be had: whoever asked for the menu is not left waiting.</summary>
        public void Failed()
        {
            pending = null; cur = null; shown = false;
            Interlocked.Exchange(ref open, 0); closed.Set();
        }

        // ---- opening ----
        public void BeginOpen(IntPtr anchor, string message, long t0)
        {
            var st = new OpenState(anchor, message, t0);
            if (!ready || core is null || controller is null) { pending = st; return; }   // asked while the page is still loading: opens when it is up
            cur = st;
            Native.KillTimer(hwnd, T_IDLE);
            if (suspended || core.IsSuspended) { core.Resume(); suspended = false; }
            var mon = TablePopup.W.MonitorFromWindow(anchor, anchor == IntPtr.Zero ? 1u /* PRIMARY */ : 2u /* NEAREST */);
            var mi = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
            Native.GetMonitorInfo(mon, ref mi);
            st.Work = Rectangle.FromLTRB(mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Right, mi.rcWork.Bottom);
            if (st.Work.IsEmpty) st.Work = new Rectangle(0, 0, 1920, 1080);
            var dpi = TablePopup.W.GetDpiForMonitor(mon, 0 /* EFFECTIVE */, out var dx, out _) == 0 && dx > 0 ? dx / 96.0 : 1.0;
            st.Scale = Scale(dpi, st.Work.Size);
            st.SizePx = new Size((int)Math.Ceiling(LogicalWidth * st.Scale), (int)Math.Ceiling(LogicalHeight * st.Scale));
            SizeWindow(st.Scale, st.SizePx);
            controller.IsVisible = true;
            core.PostWebMessageAsJson(message);
            Native.SetTimer(hwnd, T_SHAPE, ShapeTimeoutMs, IntPtr.Zero);
        }

        void SizeWindow(double scale, Size px)
        {
            Native.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, px.Width, px.Height, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
            if (controller is null) return;
            controller.RasterizationScale = scale;
            controller.Bounds = new Rectangle(0, 0, px.Width, px.Height);
        }

        // ---- page messages ----
        void OnPageMessage(string json)
        {
            var m = ParseMessage(json);
            if (m is null) { Log.Info("hotkey menu: unknown page message: " + (json.Length > 200 ? json[..200] : json)); return; }
            switch (m.Type)
            {
                case "ready": readyTcs.TrySetResult(); break;
                case "shape": OnShape(m); break;
                case "close": Close("page"); break;
                case "update":
                    Close("update");
                    var click = UpdateClicked;
                    if (click is not null)
                        ThreadPool.QueueUserWorkItem(_ => { try { click(); } catch (Exception e) { Log.Info("hotkey menu: UpdateClicked failed: " + e); } });
                    break;
            }
        }

        void OnShape(PageMessage m)
        {
            var st = cur;
            if (st is null) return; // a stale shape from a closed showing
            // The page reports its viewport in AnchorX/Y: a shape laid out before the window reached its size is not the menu's final one (the next follows).
            if (Math.Abs(m.AnchorX * st.Scale - st.SizePx.Width) > 3 || Math.Abs(m.AnchorY * st.Scale - st.SizePx.Height) > 3) return;
            var rects = TablePopup.RegionRects(m.Rects, st.Scale);
            if (rects.Count == 0) return;
            TablePopup.ApplyRegion(hwnd, rects);
            Native.KillTimer(hwnd, T_SHAPE);
            var vis = Rectangle.FromLTRB(rects.Min(r => r.Left), rects.Min(r => r.Top), rects.Max(r => r.Right), rects.Max(r => r.Bottom));
            // the cards' box (not the whole window) goes to the middle of the monitor's work area
            int x = st.Work.Left + (st.Work.Width - vis.Width) / 2 - vis.Left;
            int y = st.Work.Top + (st.Work.Height - vis.Height) / 2 - vis.Top;
            if (TestOffScreen) x = -32000;
            if (st.Shown)   // a later shape (the update row appeared): the cards changed, stay centred
            {
                Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
                return;
            }
            st.Shown = true; LastScale = st.Scale;
            st.Showing = true;   // every (de)activation while showing is part of showing, not a click-away
            Native.SetWindowPos(hwnd, HWND_TOPMOST, x, y, st.SizePx.Width, st.SizePx.Height, SWP_NOACTIVATE);
            Native.ShowWindow(hwnd, TestOffScreen ? SW_SHOWNOACTIVATE : SW_SHOW);
            if (!TestOffScreen)
            {
                TablePopup.BringToForeground(hwnd);
                controller!.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
            }
            st.Showing = false;
            st.HasBeenActive = !TestOffScreen && Native.GetForegroundWindow() == hwnd;
            LastOpenToShownMs = Stopwatch.GetElapsedTime(st.T0).TotalMilliseconds;
            shown = true;   // last: a waiting thread reads the measurements as soon as this is set
            Log.Info($"hotkey menu: open->shown {LastOpenToShownMs:0.0} ms (scale {st.Scale:0.##}, window {x},{y} {st.SizePx.Width}x{st.SizePx.Height})");
        }

        /// <summary>Hides the menu (whoever closed it); a no-op when it is not open.</summary>
        public void Close(string why)
        {
            if (pending is not null) { pending = null; Interlocked.Exchange(ref open, 0); closed.Set(); return; }   // closed before the page was up
            if (cur is null) return;
            Log.Info("hotkey menu: closed (" + why + ")");
            Native.KillTimer(hwnd, T_SHAPE);
            cur = null; shown = false;
            Native.ShowWindow(hwnd, SW_HIDE);
            if (controller is not null) controller.IsVisible = false;
            try { core?.PostWebMessageAsJson("{\"type\":\"reset\"}"); } catch (Exception e) { Log.Info("hotkey menu: reset failed: " + e.Message); }
            Native.SetTimer(hwnd, T_IDLE, (uint)IdleSuspendMs, IntPtr.Zero);
            Interlocked.Exchange(ref open, 0);
            closed.Set();
        }

        public Task<string> Eval(string js) => core is null ? Task.FromResult("null") : core.ExecuteScriptAsync(js);

        public async Task Capture(string path)
        {
            if (core is null) throw new InvalidOperationException("the page is not up");
            await using var fs = File.Create(path);
            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, fs);
        }

        // ---- click-away ----
        public void OnActivate(int state, IntPtr other)
        {
            var st = cur;
            if (st is null || !st.Shown || st.Showing) return;
            if (state != 0) { st.HasBeenActive = true; return; }
            if (!st.HasBeenActive) return; // the deactivation that comes with being shown
            if (other != IntPtr.Zero && (other == hwnd || TablePopup.W.IsChild(hwnd, other) || TablePopup.W.GetWindow(other, 4 /* OWNER */) == hwnd)) return;
            Close("deactivated");
        }

        public void OnDeactivated()
        {
            if (cur is { Shown: true, HasBeenActive: true, Showing: false }) Close("app deactivated");
        }

        // ---- timers, failures, shutdown ----
        public void OnTimer(uint id)
        {
            Native.KillTimer(hwnd, id);
            switch (id)
            {
                case T_SHAPE when cur is { Shown: false }:
                    Log.Info("hotkey menu: the page did not answer open in 5 s");
                    Close("timeout");
                    break;
                case T_IDLE when cur is null && core is not null && !core.IsSuspended:
                    _ = SuspendAsync();
                    break;
            }
        }

        async Task SuspendAsync()
        {
            try { suspended = await core!.TrySuspendAsync(); }
            catch (Exception e) { Log.Info("hotkey menu: suspend failed: " + e.Message); }
        }

        void OnProcessFailed(string kind)
        {
            Log.Info("hotkey menu: WebView2 process failed: " + kind);
            broken = true; ready = false;
            Close("failed");
        }

        public void Teardown()
        {
            Close("shutdown");
            try { controller?.Close(); } catch (Exception e) { Log.Info("hotkey menu: close failed: " + e.Message); }
            controller = null; core = null;
            if (hwnd != IntPtr.Zero) Native.DestroyWindow(hwnd);
        }
    }
}
