using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using Xunit.Abstractions;

namespace WindowManager.Tests;

// The WebView2 popup host (TablePopup.cs). The three pure tests need nothing; the live one runs a real WebView2 against a tiny test page,
// off-screen (x = -32000), never activated, no input sent: it must be safe to run while someone is using the desktop.
[Collection("Log")] // calls the static Log.Init; must not run alongside DiagnosticsTests
public class TablePopupTests(ITestOutputHelper output)
{
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("gdi32.dll")] static extern IntPtr CreateRectRgn(int l, int t, int r, int b);
    [DllImport("gdi32.dll")] static extern int DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] static extern int GetRgnBox(IntPtr rgn, out RECT box);
    [DllImport("user32.dll")] static extern int GetWindowRgn(IntPtr h, IntPtr rgn);
    [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

    [Fact]
    public void Page_messages_parse_ready_shape_and_close()
    {
        var ready = TablePopup.ParseMessage("""{"type":"ready"}""");
        Assert.Equal("ready", ready!.Type);

        var shape = TablePopup.ParseMessage("""{"type":"shape","rects":[{"x":10,"y":20.5,"w":300,"h":200,"r":12},{"x":50,"y":150,"w":200,"h":100}],"anchor":{"x":24.5,"y":36}}""");
        Assert.Equal("shape", shape!.Type);
        Assert.Equal(2, shape.Rects.Count);
        Assert.Equal(new CardRect(10, 20.5, 300, 200, 12), shape.Rects[0]);
        Assert.Equal(new CardRect(50, 150, 200, 100, 0), shape.Rects[1]); // r missing: square corners
        Assert.Equal((24.5, 36.0), (shape.AnchorX, shape.AnchorY));

        var close = TablePopup.ParseMessage("""{"type":"close","text":"| a |\n| - |","reason":"collect","asText":true}""");
        Assert.Equal("close", close!.Type);
        Assert.Equal("| a |\n| - |", close.Text);
        Assert.Equal("collect", close.Reason);
        Assert.True(close.AsText);
        Assert.False(TablePopup.ParseMessage("""{"type":"close","text":"x","reason":"escape"}""")!.AsText);

        // a page that posts a JSON string instead of an object still works; anything else is not ours
        Assert.Equal("ready", TablePopup.ParseMessage("\"{\\\"type\\\":\\\"ready\\\"}\"")!.Type);
        Assert.Null(TablePopup.ParseMessage("not json"));
        Assert.Null(TablePopup.ParseMessage("[1,2]"));
        Assert.Null(TablePopup.ParseMessage("""{"type":"bogus"}"""));
        Assert.Null(TablePopup.ParseMessage("""{"nope":1}"""));
    }

    [Fact]
    public void Region_rects_scale_with_the_dpi()
    {
        var cards = new[] { new CardRect(10, 20, 300, 200, 12), new CardRect(50.5, 150.25, 100, 40, 0) };

        var at100 = TablePopup.RegionRects(cards, 1.0);
        Assert.Equal(new RegionRect(10, 20, 310, 220, 12), at100[0]);
        Assert.Equal(new RegionRect(50, 150, 151, 191, 0), at100[1]); // edges round outwards: 50.5 -> 50, 150.5 -> 151, 150.25 -> 150, 190.25 -> 191

        var at150 = TablePopup.RegionRects(cards, 1.5);
        Assert.Equal(new RegionRect(15, 30, 465, 330, 18), at150[0]);
        Assert.Equal(new RegionRect(75, 225, 226, 286, 0), at150[1]); // 75.75 -> 75, 225.375 -> 225, 225.75 -> 226, 285.375 -> 286

        var at200 = TablePopup.RegionRects(cards, 2.0);
        Assert.Equal(new RegionRect(20, 40, 620, 440, 24), at200[0]);
        Assert.Empty(TablePopup.RegionRects([], 1.5));
    }

    [Fact]
    public void The_anchor_lands_on_the_caret_and_the_window_stays_on_the_monitor()
    {
        var work = new Rectangle(0, 0, 2560, 1400);   // a monitor's work area
        var win = new Size(1350, 960);                // 900x640 at 150%
        var anchor = new PointF(24, 36);              // where the page's first text line starts, CSS px

        // plenty of room: the anchor (x1.5) lands exactly on the caret's top-left
        var p = TablePopup.PlaceWindow(anchor, 1.5, new Rectangle(400, 300, 2, 20), work, win);
        Assert.Equal(new Point(400 - 36, 300 - 54), p);

        // caret near the right and bottom edge: the whole window is pulled back inside
        p = TablePopup.PlaceWindow(anchor, 1.5, new Rectangle(2500, 1380, 2, 20), work, win);
        Assert.Equal(new Point(2560 - 1350, 1400 - 960), p);

        // caret near the top-left: the window top-left is held at the work area's
        p = TablePopup.PlaceWindow(anchor, 1.5, new Rectangle(5, 5, 2, 20), work, win);
        Assert.Equal(new Point(0, 0), p);

        // a second monitor to the left of the primary (negative x) and a work area that starts below a taskbar
        var left = new Rectangle(-1920, 40, 1920, 1040);
        p = TablePopup.PlaceWindow(new PointF(0, 0), 1.0, new Rectangle(-30, 1000, 2, 20), left, new Size(900, 640));
        Assert.Equal(new Point(-900, 1080 - 640), p);
        Assert.True(left.Contains(new Rectangle(p, new Size(900, 640))));

        // only the cards count when their box is given: the transparent rest of the window may overhang the monitor
        var cards = new Rectangle(30, 50, 400, 300);
        var small = new Rectangle(0, 0, 1200, 900);
        p = TablePopup.PlaceWindow(anchor, 1.0, new Rectangle(700, 500, 2, 20), small, new Size(900, 640), cards);
        Assert.Equal(new Point(700 - 24, 500 - 36), p);        // window right edge is 1576 > 1200, but the cards end at 1106: no clamp
        p = TablePopup.PlaceWindow(anchor, 1.0, new Rectangle(1000, 500, 2, 20), small, new Size(900, 640), cards);
        Assert.Equal(new Point(1200 - 430, 500 - 36), p);      // the cards would end at 1406: pulled back to the edge
    }

    const string TestPage = """
        <!doctype html><meta charset="utf-8"><title>test page</title>
        <body style="margin:0;background:transparent"><div style="width:300px;height:200px;margin:10px;background:#c33"></div>
        <script>
        let text = "";
        chrome.webview.addEventListener("message", e => {
          const m = e.data;
          if (m.type === "open") {
            text = m.text;
            chrome.webview.postMessage({ type: "shape", rects: [{ x: 10, y: 10, w: 300, h: 200, r: 12 }, { x: 50, y: 150, w: 200, h: 100, r: 6 }], anchor: { x: 20, y: 30 } });
          } else if (m.type === "collect") {
            chrome.webview.postMessage({ type: "close", text: text + "!", reason: "collect", asText: false });
          }
        });
        chrome.webview.postMessage({ type: "ready" });
        </script>
        """;

    [Fact]
    [Trait("Category", "Live")]
    public async Task Prewarm_then_open_and_collect_round_trip_off_screen()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "wm-popup-test-" + Guid.NewGuid().ToString("N")[..8]);
        var web = Path.Combine(tmp, "web");
        Directory.CreateDirectory(web);
        File.WriteAllText(Path.Combine(web, "editor.html"), TestPage);
        Log.Init(tmp, verbose: false);
        TablePopup.TestOffScreen = true;
        TablePopup.UserDataFolderOverride = Path.Combine(tmp, "userdata"); // never the real profile
        TablePopup.IdleSuspendMs = 600;
        var foregroundBefore = GetForegroundWindow();
        try
        {
            var sw = Stopwatch.StartNew();
            TablePopup.Prewarm(web);
            Assert.False(TablePopup.IsShown);

            // first open waits for the cold start
            var caret = new Rectangle(300, 200, 2, 20);
            var first = TablePopup.Open("hello", 3, caret);
            await WaitFor(() => TablePopup.IsShown, 60_000, "the popup to be shown");
            output.WriteLine($"cold prewarm+open to shown: {sw.ElapsedMilliseconds} ms (open->shape {TablePopup.LastOpenToShapeMs:0.0} ms)");
            Assert.True(TablePopup.IsReady);
            Assert.True(IsWindowVisible(TablePopup.WindowHandle));
            Assert.NotEqual(TablePopup.WindowHandle, GetForegroundWindow());

            // the window region is the union of the two cards (CSS 10,10 300x200 and 50,150 200x100)
            var box = WindowRegionBox(TablePopup.WindowHandle);
            var s = TablePopup.LastScale;
            Assert.Equal((int)Math.Floor(10 * s), box.Left);
            Assert.Equal((int)Math.Floor(10 * s), box.Top);
            Assert.Equal((int)Math.Ceiling(310 * s), box.Right);
            Assert.Equal((int)Math.Ceiling(250 * s), box.Bottom);

            TablePopup.TestDeactivate();
            var r1 = await first.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(new PopupResult("hello!", "collect", false), r1);
            Assert.False(TablePopup.IsShown);
            Assert.False(IsWindowVisible(TablePopup.WindowHandle));

            // warm opens
            var shapeMs = new List<double>();
            for (int i = 0; i < 5; i++)
            {
                var open = TablePopup.Open("round " + i, 0, caret);
                await WaitFor(() => TablePopup.IsShown, 10_000, "a warm open");
                shapeMs.Add(TablePopup.LastOpenToShapeMs);
                output.WriteLine($"warm open {i}: shape {TablePopup.LastOpenToShapeMs:0.0} ms, shown {TablePopup.LastOpenToShownMs:0.0} ms");
                TablePopup.TestDeactivate();
                Assert.Equal(new PopupResult("round " + i + "!", "collect", false), await open.WaitAsync(TimeSpan.FromSeconds(10)));
            }
            Assert.True(shapeMs.Max() < 1000, "warm open -> shape: " + string.Join(", ", shapeMs.Select(m => m.ToString("0.0"))) + " ms");

            // idle: the hidden page suspends, and the next open resumes it
            await WaitFor(() => TablePopup.IsSuspended, 10_000, "the idle suspend");
            var afterSuspend = TablePopup.Open("resumed", 0, caret);
            await WaitFor(() => TablePopup.IsShown, 10_000, "the open after a suspend");
            output.WriteLine($"open after suspend: resume {TablePopup.LastResumeMs:0.0} ms, shape {TablePopup.LastOpenToShapeMs:0.0} ms, shown {TablePopup.LastOpenToShownMs:0.0} ms");
            TablePopup.TestDeactivate();
            Assert.Equal(new PopupResult("resumed!", "collect", false), await afterSuspend.WaitAsync(TimeSpan.FromSeconds(10)));

            Assert.NotEqual(TablePopup.WindowHandle, GetForegroundWindow()); // the popup never took the foreground (other windows may, on a live desktop)
        }
        finally
        {
            TablePopup.Shutdown();
            TablePopup.TestOffScreen = false;
            TablePopup.UserDataFolderOverride = null;
            TablePopup.IdleSuspendMs = 30_000;
            try { output.WriteLine(File.ReadAllText(Path.Combine(tmp, "wm.log"))); } catch (IOException) { }
            await Task.Delay(500); // let the browser processes of the temp profile exit before the folder goes
            try { Directory.Delete(tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // The popup thread is per-monitor-v2; this (xunit) thread is not, so Windows would scale the region back. Read it as the popup sees it.
    static RECT WindowRegionBox(IntPtr hwnd)
    {
        var old = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try
        {
            var rgn = CreateRectRgn(0, 0, 0, 0);
            Assert.NotEqual(0, GetWindowRgn(hwnd, rgn));
            GetRgnBox(rgn, out var box);
            DeleteObject(rgn);
            return box;
        }
        finally { SetThreadDpiAwarenessContext(old); }
    }

    static async Task WaitFor(Func<bool> cond, int ms, string what)
    {
        var until = Environment.TickCount64 + ms;
        while (!cond())
        {
            if (Environment.TickCount64 > until) throw new TimeoutException("timed out waiting for " + what);
            await Task.Delay(10);
        }
    }
}
