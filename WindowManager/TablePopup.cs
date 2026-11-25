using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace WindowManager;

/// <summary>One card on the page (tile, ribbon, picker, with its hard shadow), in CSS px, and its corner radius.</summary>
public sealed record CardRect(double X, double Y, double W, double H, double R);

/// <summary>A card in window pixels. Right and Bottom are exclusive. Radius is the corner radius in px.</summary>
public sealed record RegionRect(int Left, int Top, int Right, int Bottom, int Radius);

/// <summary>What the popup hands back when it closes. Reason: "escape", "collect" (clicked away), "timeout" or "failed".</summary>
public sealed record PopupResult(string Text, string Reason, bool AsText);

/// <summary>One page-to-host message, already parsed. Type is "ready", "shape" or "close"; the other fields belong to their type.</summary>
public sealed record PageMessage(string Type, IReadOnlyList<CardRect> Rects, double AnchorX, double AnchorY, string Text, string Reason, bool AsText);

/// <summary>
/// The table editor as a WebView2 popup (plan: DevResources/plans/table-editor-webview2-2026-11-20.md). One dedicated STA thread owns a raw
/// Win32 popup window with a WebView2 controller (Core API only, no WinForms) and its own message loop; the page is created and loaded once
/// (Prewarm), kept hidden, and shown next to the caret by Open. Everything on that thread is "pt" state; the public API is thread-safe.
/// </summary>
public static class TablePopup
{
    public const string Host = "wm.table";
    public const double LogicalWidth = 900, LogicalHeight = 640;

    // ---- test switches and measurements (public: the test project is a separate assembly) ----
    /// <summary>Test only: never activate or focus, show with SW_SHOWNOACTIVATE and force x = -32000.</summary>
    public static bool TestOffScreen { get; set; }
    /// <summary>Test only: a user-data folder other than %LOCALAPPDATA%\WindowManager\WebView2 (read by the next Prewarm).</summary>
    public static string? UserDataFolderOverride { get; set; }
    /// <summary>How long the popup stays hidden before the page is suspended. Settable so a test need not wait 30 s.</summary>
    public static int IdleSuspendMs { get; set; } = 30_000;
    public static double LastOpenToShapeMs { get; private set; }
    public static double LastOpenToShownMs { get; private set; }
    public static double LastResumeMs { get; private set; }
    public static bool IsSuspended => suspended;
    /// <summary>Test only: the DPI scale of the last open, and the popup window.</summary>
    public static double LastScale { get; private set; }
    public static IntPtr WindowHandle => hwnd;
    /// <summary>True from the moment the window is shown until the page closes it.</summary>
    public static bool IsShown => shown;

    public static bool IsReady => ready;

    /// <summary>The caller is writing the table into the app (focus goes there and comes back): focus changes are not a click-away meanwhile.</summary>
    public static volatile bool Writing;

    /// <summary>After a write into the app: the popup takes the focus back, the grid has the keyboard again.</summary>
    public static void Refocus()
    {
        using var done = new ManualResetEventSlim();
        Post(() => { try { pt.Refocus(); } finally { done.Set(); } });
        done.Wait(1000);
    }

    // ---- shared state (any thread) ----
    static readonly object startLock = new();
    static Thread? thread;
    static volatile bool ready;
    static volatile bool suspended;
    static volatile bool broken;
    static volatile bool shown;
    static IntPtr hwnd;                                   // written by the popup thread before windowUp is set
    static ManualResetEventSlim windowUp = new();
    static TaskCompletionSource readyTcs = NewReadyTcs();
    static readonly SemaphoreSlim gate = new(1, 1);       // one open at a time
    static readonly ConcurrentQueue<Action> work = new();

    static TaskCompletionSource NewReadyTcs() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // ================= pure helpers =================

    /// <summary>Parses a message the page posted (the WebMessageAsJson text). Null when it is not one of ours.</summary>
    public static PageMessage? ParseMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.String) return ParseMessage(root.GetString() ?? ""); // the page posted a string
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.String) return null;
            var type = t.GetString()!;
            switch (type)
            {
                case "ready":
                    return new PageMessage(type, [], 0, 0, "", "", false);
                case "shape":
                {
                    var rects = new List<CardRect>();
                    if (root.TryGetProperty("rects", out var arr) && arr.ValueKind == JsonValueKind.Array)
                        foreach (var e in arr.EnumerateArray())
                            rects.Add(new CardRect(Num(e, "x"), Num(e, "y"), Num(e, "w"), Num(e, "h"), Num(e, "r")));
                    double ax = 0, ay = 0;
                    if (root.TryGetProperty("anchor", out var a) && a.ValueKind == JsonValueKind.Object) { ax = Num(a, "x"); ay = Num(a, "y"); }
                    return new PageMessage(type, rects, ax, ay, "", "", false);
                }
                case "activity":
                    return new PageMessage(type, [], 0, 0, "", root.TryGetProperty("editing", out var ed) && ed.ValueKind == JsonValueKind.True ? "editing" : "", false);
                case "change":
                    return new PageMessage(type, [], 0, 0, root.TryGetProperty("text", out var ct) && ct.ValueKind == JsonValueKind.String ? ct.GetString()! : "", "", false);
                case "close":
                {
                    var text = root.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String ? tx.GetString()! : "";
                    var reason = root.TryGetProperty("reason", out var rs) && rs.ValueKind == JsonValueKind.String ? rs.GetString()! : "";
                    var asText = root.TryGetProperty("asText", out var at) && at.ValueKind == JsonValueKind.True;
                    return new PageMessage(type, [], 0, 0, text, reason, asText);
                }
                default:
                    return null;
            }
        }
        catch (JsonException) { return null; }

        static double Num(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
    }

    /// <summary>The cards in window pixels: edges rounded outwards (so the shadow is never clipped), the radius rounded to nearest.</summary>
    public static IReadOnlyList<RegionRect> RegionRects(IEnumerable<CardRect> cssRects, double scale) =>
        cssRects.Select(c => new RegionRect(
            (int)Math.Floor(c.X * scale), (int)Math.Floor(c.Y * scale),
            (int)Math.Ceiling((c.X + c.W) * scale), (int)Math.Ceiling((c.Y + c.H) * scale),
            (int)Math.Round(c.R * scale))).ToList();

    /// <summary>
    /// Where the window's top-left goes so that the page's anchor (CSS px, times the scale) lands on the caret's top-left, then clamped so the
    /// window stays inside the monitor's work area. visiblePx (the cards' bounding box inside the window) makes the clamp keep what is visible on
    /// the monitor rather than the whole generous window, so the cards are not pushed off the caret when only transparent window area overhangs.
    /// </summary>
    public static Point PlaceWindow(PointF anchorCss, double scale, Rectangle caret, Rectangle monitorWork, Size windowPx, Rectangle? visiblePx = null)
    {
        var vis = visiblePx ?? new Rectangle(0, 0, windowPx.Width, windowPx.Height);
        int x = caret.Left - (int)Math.Round(anchorCss.X * scale);
        int y = caret.Top - (int)Math.Round(anchorCss.Y * scale);
        x = Math.Max(Math.Min(x, monitorWork.Right - vis.Right), monitorWork.Left - vis.Left);   // too big for the monitor: top-left wins
        y = Math.Max(Math.Min(y, monitorWork.Bottom - vis.Bottom), monitorWork.Top - vis.Top);
        return new Point(x, y);
    }

    /// <summary>Clips the window to the union of the cards (round-cornered where they are). Shared with HelpPopup.</summary>
    internal static void ApplyRegion(IntPtr hwnd, IReadOnlyList<RegionRect> rects)
    {
        if (rects.Count == 0) return;
        IntPtr all = IntPtr.Zero;
        foreach (var r in rects)
        {
            var one = r.Radius > 0 ? W.CreateRoundRectRgn(r.Left, r.Top, r.Right + 1, r.Bottom + 1, r.Radius * 2, r.Radius * 2) /* GDI leaves the last row and column off a round-rect region */ : W.CreateRectRgn(r.Left, r.Top, r.Right, r.Bottom);
            if (all == IntPtr.Zero) { all = one; continue; }
            W.CombineRgn(all, all, one, 2 /* OR */);
            Native.DeleteObject(one);
        }
        if (Native.SetWindowRgn(hwnd, all, true) == 0) Native.DeleteObject(all); // on success the system owns the region
    }

    /// <summary>Takes the foreground for the popup window (the foreground lock needs the unlock below). Shared with HelpPopup.</summary>
    internal static void BringToForeground(IntPtr hwnd)
    {
        // Windows' foreground lock refuses a process without recent input; an injected tap of the unassigned mask key counts as input
        // (the manager's hook ignores injected keys, apps ignore 0xE8). Same unlock as Program.TestActivate.
        Desk.SendKeys(KeyEngine.Tap(Vk.Mask));
        if (Native.SetForegroundWindow(hwnd) && Native.GetForegroundWindow() == hwnd) return;
        var fg = Native.GetForegroundWindow();
        uint fgThread = fg == IntPtr.Zero ? 0 : Native.GetWindowThreadProcessId(fg, out _);
        uint me = Native.GetCurrentThreadId();
        // Windows only lets the foreground process hand out the foreground; borrow its input queue for the call.
        bool attached = fgThread != 0 && fgThread != me && Native.AttachThreadInput(me, fgThread, true);
        try
        {
            Native.BringWindowToTop(hwnd);
            Native.SetForegroundWindow(hwnd);
        }
        finally { if (attached) Native.AttachThreadInput(me, fgThread, false); }
        if (Native.GetForegroundWindow() != hwnd) Log.Info("popup: SetForegroundWindow did not take the foreground");
    }

    // ================= public API =================

    /// <summary>Starts the popup thread: window, WebView2, the page. Returns at once; IsReady turns true when the page said "ready".</summary>
    public static void Prewarm(string? webFolder = null)
    {
        lock (startLock)
        {
            if (thread is not null) return;
            ready = false; broken = false;
            readyTcs = NewReadyTcs();
            windowUp = new ManualResetEventSlim();
            thread = new Thread(() => ThreadMain(webFolder ?? Path.Combine(AppContext.BaseDirectory, "web"))) { IsBackground = true, Name = "TablePopup" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
        windowUp.Wait(5000);
    }

    /// <summary>
    /// Shows the editor with the text and caret next to caretScreenRect (screen px) and completes when the user is done. Waits for the page
    /// when it is still starting (cold start); throws if WebView2 cannot be started at all.
    /// </summary>
    public static async Task<PopupResult> Open(string text, int caret, Rectangle caretScreenRect, Action<string>? onChange = null, Action<bool>? onActivity = null)
    {
        if (broken) Shutdown();
        Prewarm();
        await readyTcs.Task.ConfigureAwait(false);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var tcs = new TaskCompletionSource<PopupResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var t0 = Stopwatch.GetTimestamp();
            Post(() => pt.BeginOpen(text, caret, caretScreenRect, tcs, t0, onChange, onActivity));
            return await tcs.Task.ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    /// <summary>Test only: runs a script in the page and returns its JSON result (where an element is, to click it).</summary>
    public static string? EvalForTest(string js, int timeoutMs = 2000)
    {
        string? result = null;
        using var done = new ManualResetEventSlim();
        Post(async () => { try { result = await pt.Eval(js); } catch (Exception e) { result = "error: " + e.Message; } finally { done.Set(); } });
        done.Wait(timeoutMs);
        return result;
    }

    /// <summary>Test only: what the host does when the popup is deactivated while open: ask the page to hand its text back.</summary>
    public static void TestDeactivate() => Post(() => pt.RequestCollect("test"));

    /// <summary>Closes the controller and the window and ends the thread.</summary>
    public static void Shutdown()
    {
        Thread? t;
        lock (startLock) { t = thread; thread = null; }
        if (t is null) return;
        Post(() => pt.Teardown());
        if (!t.Join(5000)) Log.Info("table popup: thread did not end in 5 s");
        ready = false; suspended = false;
        readyTcs.TrySetException(new ObjectDisposedException(nameof(TablePopup)));
    }

    static void Post(Action a)
    {
        work.Enqueue(a);
        if (hwnd != IntPtr.Zero) Native.PostMessage(hwnd, WM_RUN, IntPtr.Zero, IntPtr.Zero);
    }

    // ================= the popup thread =================

    const uint WM_RUN = 0x8000 + 1, WM_ACTIVATE = 0x0006, WM_ACTIVATEAPP = 0x001C, WM_DPICHANGED = 0x02E0, WM_TIMER = 0x0113, WM_CLOSE = 0x0010, WM_DESTROY = 0x0002;
    const uint WS_POPUP = 0x80000000, WS_EX_TOOLWINDOW = 0x80, WS_EX_TOPMOST = 0x8;
    const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOZORDER = 4, SWP_NOACTIVATE = 0x10;
    const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4, SW_SHOW = 5;
    static readonly IntPtr HWND_TOPMOST = new(-1), DPI_PMV2 = new(-4);
    const uint T_SHAPE = 1, T_COLLECT = 2, T_IDLE = 3;
    const int ShapeTimeoutMs = 5000, CollectTimeoutMs = 2000;

    static readonly Native.WndProc wndProc = WndProc;          // kept alive for the window class
    static readonly Popup pt = new();                          // touched only on the popup thread

    static void ThreadMain(string webFolder)
    {
        try
        {
            W.SetThreadDpiAwarenessContext(DPI_PMV2); // the popup is per-monitor-v2 even in a host process that is not (the test runner)
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
        catch (Exception e) { Log.Info($"table popup: thread failed: {e}"); readyTcs.TrySetException(e); }
        finally { windowUp.Set(); hwnd = IntPtr.Zero; }
    }

    static IntPtr WndProc(IntPtr h, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_RUN:
                while (work.TryDequeue(out var a))
                {
                    try { a(); } catch (Exception e) { Log.Info($"table popup: work item failed: {e}"); }
                }
                return IntPtr.Zero;
            case WM_ACTIVATE:
                pt.OnActivate((int)((long)wParam & 0xFFFF), lParam);
                break;
            case WM_ACTIVATEAPP:
                if (wParam == IntPtr.Zero) pt.OnDeactivated();
                break;
            case WM_DPICHANGED:
                return IntPtr.Zero; // we size the window ourselves, from the caret monitor's scale; ignore the suggested rect
            case WM_TIMER:
                pt.OnTimer((uint)(long)wParam);
                return IntPtr.Zero;
            case WM_CLOSE:
                pt.RequestCollect("wm_close"); // Alt+F4 and friends: same as clicking away
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
        public override void Post(SendOrPostCallback d, object? state) => TablePopup.Post(() => d(state));
        public override void Send(SendOrPostCallback d, object? state)
        {
            using var done = new ManualResetEventSlim();
            TablePopup.Post(() => { try { d(state); } finally { done.Set(); } });
            done.Wait();
        }
    }

    sealed class OpenState(string text, Rectangle caret, TaskCompletionSource<PopupResult> tcs, long t0)
    {
        public readonly string Text = text;
        public readonly Rectangle Caret = caret;
        public readonly TaskCompletionSource<PopupResult> Tcs = tcs;
        public readonly long T0 = t0;
        public double Scale = 1;
        public Size SizePx;
        public Rectangle Work;
        public bool Shown, HasBeenActive, Collecting, Showing;
        public Action<string>? OnChange;
        public Action<bool>? OnActivity;   // a key in the page; true while a cell is being edited
    }

    sealed class Popup
    {
        CoreWebView2Controller? controller;
        CoreWebView2? core;
        OpenState? cur;

        public void CreateWindow()
        {
            var wc = new Native.WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEX>(),
                lpfnWndProc = wndProc,
                hInstance = Native.GetModuleHandle(null),
                hbrBackground = new IntPtr(6), // COLOR_WINDOW+1: what shows where the page is transparent (outside the region nothing shows at all)
                lpszClassName = "WmTablePopup",
            };
            if (Native.RegisterClassEx(ref wc) == 0 && Marshal.GetLastWin32Error() != 1410) // 1410 = class already registered (Prewarm after Shutdown)
                throw new InvalidOperationException("RegisterClassEx failed: " + Marshal.GetLastWin32Error());
            hwnd = Native.CreateWindowEx(WS_EX_TOOLWINDOW | WS_EX_TOPMOST, wc.lpszClassName, "Table editor", WS_POPUP,
                -32000, -32000, (int)LogicalWidth, (int)LogicalHeight, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
            if (hwnd == IntPtr.Zero) throw new InvalidOperationException("CreateWindowEx failed: " + Marshal.GetLastWin32Error());
            // No open/close animation: Windows fades and zooms every shown window in, which made the editor read as "a window opening"
            // instead of the grid appearing in place over the text (the owner, 2026-11-20). DWMWA_TRANSITIONS_FORCEDISABLED = 3.
            uint off = 1; Native.DwmSetWindowAttribute(hwnd, 3, ref off, sizeof(uint));
        }

        // ---- startup ----
        public async Task InitAsync(string webFolder)
        {
            var sw = Stopwatch.StartNew();
            long last = 0;
            void Phase(string what) { var now = sw.ElapsedMilliseconds; Log.Info($"table popup: {what} {now - last} ms (total {now} ms)"); last = now; }
            try
            {
                if (!File.Exists(Path.Combine(webFolder, "editor.html"))) throw new FileNotFoundException("no editor.html in " + webFolder);
                // Alpha 00: outside the cards the page is transparent (the window region clips to the cards; this only matters for the corner pixels).
                if (Environment.GetEnvironmentVariable("WEBVIEW2_DEFAULT_BACKGROUND_COLOR") is null)
                    Environment.SetEnvironmentVariable("WEBVIEW2_DEFAULT_BACKGROUND_COLOR", "00FFFFFF");
                var data = UserDataFolderOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowManager", "WebView2");
                Directory.CreateDirectory(data);
                // No profile name: a named profile costs ~5 s on this build (WebView2Feedback #5557).
                var env = await CoreWebView2Environment.CreateAsync(null, data);
                Phase("environment");
                controller = await env.CreateCoreWebView2ControllerAsync(hwnd);
                Phase("controller");
                core = controller.CoreWebView2;
                controller.ShouldDetectMonitorScaleChanges = false; // we set RasterizationScale ourselves, per open, from the caret's monitor
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
                s.AreBrowserAcceleratorKeysEnabled = false; // Ctrl+F/P/R/F5/F12... off; copy/paste/undo/select-all are editing commands and stay
                core.SetVirtualHostNameToFolderMapping(Host, webFolder, CoreWebView2HostResourceAccessKind.Allow);
                core.NavigationStarting += (_, e) => { if (!e.Uri.StartsWith($"https://{Host}/", StringComparison.OrdinalIgnoreCase)) e.Cancel = true; };
                core.NewWindowRequested += (_, e) => e.Handled = true;
                core.PermissionRequested += (_, e) =>
                    e.State = e.PermissionKind == CoreWebView2PermissionKind.ClipboardRead ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
                core.WebMessageReceived += (_, e) => OnPageMessage(e.WebMessageAsJson);
                core.ProcessFailed += (_, e) => OnProcessFailed(e.ProcessFailedKind.ToString());
                SizeWindow(1.0, new Size((int)LogicalWidth, (int)LogicalHeight));
                controller.IsVisible = true; // loads at normal priority; hidden again once the page said ready
                Phase("settings");
                core.Navigate($"https://{Host}/editor.html");
                // The page answers on load; OnPageMessage completes readyTcs.
                await readyTcs.Task;
                Phase("navigate to ready");
                controller.IsVisible = false;
                ready = true;
                Native.SetTimer(hwnd, T_IDLE, (uint)IdleSuspendMs, IntPtr.Zero);
                Log.Info($"table popup: ready in {sw.ElapsedMilliseconds} ms");
            }
            catch (Exception e)
            {
                Log.Info($"table popup: init failed: {e}");
                readyTcs.TrySetException(e);
            }
        }

        // ---- opening ----
        public void BeginOpen(string text, int caret, Rectangle caretRect, TaskCompletionSource<PopupResult> tcs, long t0, Action<string>? onChange, Action<bool>? onActivity)
        {
            if (core is null || controller is null || !ready) { tcs.TrySetException(new InvalidOperationException("table popup is not ready")); return; }
            var st = new OpenState(text, caretRect, tcs, t0) { OnChange = onChange, OnActivity = onActivity };
            cur = st;
            Native.KillTimer(hwnd, T_IDLE);
            if (suspended || core.IsSuspended)
            {
                var r0 = Stopwatch.GetTimestamp();
                core.Resume();
                suspended = false;
                LastResumeMs = Stopwatch.GetElapsedTime(r0).TotalMilliseconds;
            }
            var pt0 = new Native.POINT { X = caretRect.Left, Y = caretRect.Top };
            var mon = W.MonitorFromPoint(pt0, 2 /* NEAREST */);
            var mi = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
            Native.GetMonitorInfo(mon, ref mi);
            st.Work = Rectangle.FromLTRB(mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Right, mi.rcWork.Bottom);
            if (st.Work.IsEmpty) st.Work = new Rectangle(-32000, -32000, 64000, 64000);
            st.Scale = W.GetDpiForMonitor(mon, 0 /* EFFECTIVE */, out var dx, out _) == 0 && dx > 0 ? dx / 96.0 : 1.0;
            st.SizePx = new Size((int)Math.Ceiling(LogicalWidth * st.Scale), (int)Math.Ceiling(LogicalHeight * st.Scale));
            SizeWindow(st.Scale, st.SizePx);
            controller.IsVisible = true;
            core.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "open", text, caret }));
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
            if (m is null) { Log.Info("table popup: unknown page message: " + (json.Length > 200 ? json[..200] : json)); return; }
            switch (m.Type)
            {
                case "ready": readyTcs.TrySetResult(); break;
                case "shape": OnShape(m); break;
                case "close": OnClose(m); break;
                case "change": cur?.OnChange?.Invoke(m.Text); break;
                case "activity": cur?.OnActivity?.Invoke(m.Reason == "editing"); break;
            }
        }

        void OnShape(PageMessage m)
        {
            var st = cur;
            if (st is null) return; // a stale shape from a closed session
            var rects = RegionRects(m.Rects, st.Scale);
            ApplyRegion(rects);
            if (st.Shown) return;   // later shapes (picker closed, ribbon appeared): region only, the window stays where it is
            Native.KillTimer(hwnd, T_SHAPE);
            LastOpenToShapeMs = Stopwatch.GetElapsedTime(st.T0).TotalMilliseconds;
            Rectangle? visible = null;
            if (rects.Count > 0)
                visible = Rectangle.FromLTRB(rects.Min(r => r.Left), rects.Min(r => r.Top), rects.Max(r => r.Right), rects.Max(r => r.Bottom));
            var pos = PlaceWindow(new PointF((float)m.AnchorX, (float)m.AnchorY), st.Scale, st.Caret, st.Work, st.SizePx, visible);
            if (TestOffScreen) pos.X = -32000;
            st.Shown = true; shown = true; LastScale = st.Scale;
            // Every (de)activation while showing is part of showing, not a click-away: taking the foreground can bounce it (measured:
            // the attach-input fallback deactivated the popup 25 ms after it appeared, and it closed itself with nothing typed).
            st.Showing = true;
            Native.SetWindowPos(hwnd, HWND_TOPMOST, pos.X, pos.Y, st.SizePx.Width, st.SizePx.Height, SWP_NOACTIVATE);
            Native.ShowWindow(hwnd, TestOffScreen ? SW_SHOWNOACTIVATE : SW_SHOW);
            if (!TestOffScreen)
            {
                BringToForeground();
                controller!.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
            }
            st.Showing = false;
            st.HasBeenActive = !TestOffScreen && Native.GetForegroundWindow() == hwnd; // click-away counts once it really has the focus
            LastOpenToShownMs = Stopwatch.GetElapsedTime(st.T0).TotalMilliseconds;
            Log.Info($"table popup: open->shape {LastOpenToShapeMs:0.0} ms, open->shown {LastOpenToShownMs:0.0} ms (scale {st.Scale}, window {pos.X},{pos.Y} {st.SizePx.Width}x{st.SizePx.Height})");
        }

        void ApplyRegion(IReadOnlyList<RegionRect> rects) => TablePopup.ApplyRegion(hwnd, rects);

        static void BringToForeground() => TablePopup.BringToForeground(hwnd);

        void OnClose(PageMessage m)
        {
            var st = cur;
            if (st is null) return;
            Hide();
            st.Tcs.TrySetResult(new PopupResult(m.Text, m.Reason, m.AsText));
        }

        void Hide()
        {
            Native.KillTimer(hwnd, T_SHAPE);
            Native.KillTimer(hwnd, T_COLLECT);
            cur = null; shown = false;
            Native.ShowWindow(hwnd, SW_HIDE);
            if (controller is not null) controller.IsVisible = false;
            Native.SetTimer(hwnd, T_IDLE, (uint)IdleSuspendMs, IntPtr.Zero);
        }

        public void Refocus()
        {
            var st = cur;
            if (st is null || !st.Shown || TestOffScreen) return;
            st.Showing = true;
            BringToForeground();
            controller?.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
            st.Showing = false;
            st.HasBeenActive = Native.GetForegroundWindow() == hwnd;
        }

        public Task<string> Eval(string js) => core is null ? Task.FromResult("null") : core.ExecuteScriptAsync(js);

        // ---- click-away ----
        public void OnActivate(int state, IntPtr other)
        {
            var st = cur;
            if (st is null || !st.Shown || st.Showing) return;
            if (state != 0) { st.HasBeenActive = true; return; }
            if (Writing) return; // the caller moved the focus to the app to write the table there
            if (!st.HasBeenActive) return; // the deactivation that comes with being shown
            // Focus moving to a window of our own (a WebView2 popup, a child) is not a click-away.
            if (other != IntPtr.Zero && (other == hwnd || W.IsChild(hwnd, other) || W.GetWindow(other, 4 /* OWNER */) == hwnd)) return;
            RequestCollect("deactivated");
        }

        public void OnDeactivated()
        {
            var st = cur;
            if (st is { Shown: true, HasBeenActive: true, Showing: false } && !Writing) RequestCollect("app deactivated");
        }

        public void RequestCollect(string why)
        {
            var st = cur;
            if (st is null || !st.Shown || st.Collecting || core is null) return;
            st.Collecting = true;
            Log.Info("table popup: collect (" + why + ")");
            core.PostWebMessageAsJson("{\"type\":\"collect\"}");
            Native.SetTimer(hwnd, T_COLLECT, CollectTimeoutMs, IntPtr.Zero);
        }

        // ---- timers, failures, shutdown ----
        public void OnTimer(uint id)
        {
            Native.KillTimer(hwnd, id);
            var st = cur;
            switch (id)
            {
                case T_SHAPE when st is { Shown: false }:
                    Log.Info("table popup: the page did not answer open in 5 s");
                    Hide();
                    st.Tcs.TrySetResult(new PopupResult(st.Text, "timeout", false));
                    break;
                case T_COLLECT when st is not null:
                    Log.Info("table popup: the page did not answer collect in 2 s");
                    Hide();
                    st.Tcs.TrySetResult(new PopupResult(st.Text, "timeout", false));
                    break;
                case T_IDLE when st is null && core is not null && !core.IsSuspended:
                    _ = SuspendAsync();
                    break;
            }
        }

        async Task SuspendAsync()
        {
            try
            {
                var t = Stopwatch.GetTimestamp();
                var ok = await core!.TrySuspendAsync();
                suspended = ok;
                Log.Info($"table popup: suspended={ok} ({Stopwatch.GetElapsedTime(t).TotalMilliseconds:0} ms)");
            }
            catch (Exception e) { Log.Info("table popup: suspend failed: " + e.Message); }
        }

        void OnProcessFailed(string kind)
        {
            Log.Info("table popup: WebView2 process failed: " + kind);
            broken = true; ready = false;
            var st = cur;
            if (st is not null) { Hide(); st.Tcs.TrySetResult(new PopupResult(st.Text, "failed", false)); }
        }

        public void Teardown()
        {
            var st = cur;
            if (st is not null) { Hide(); st.Tcs.TrySetResult(new PopupResult(st.Text, "failed", false)); }
            try { controller?.Close(); } catch (Exception e) { Log.Info("table popup: close failed: " + e.Message); }
            controller = null; core = null;
            if (hwnd != IntPtr.Zero) Native.DestroyWindow(hwnd);
        }
    }

    internal static class W
    {
        [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(Native.POINT pt, uint flags);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr wnd, uint flags);
        [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr mon, int type, out uint x, out uint y);
        [DllImport("user32.dll")] public static extern bool IsChild(IntPtr parent, IntPtr wnd);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr wnd, uint cmd);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateRectRgn(int l, int t, int r, int b);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
        [DllImport("gdi32.dll")] public static extern int CombineRgn(IntPtr dst, IntPtr a, IntPtr b, int mode);
    }
}
