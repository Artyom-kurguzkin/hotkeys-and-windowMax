using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Xunit.Abstractions;

namespace WindowManager.Tests;

// The hotkey menu (HelpPopup.cs, web/hotkeys.html) and the version it shows (AppVersion.cs). The first three tests are pure; the live one runs a
// real WebView2 against the real page, off-screen (x = -32000), never activated, no input sent: safe to run while someone is using the desktop.
[Collection("Log")] // calls the static Log.Init and sets HelpPopup's static hooks; must not run alongside DiagnosticsTests or TablePopupTests
public class HelpPopupTests(ITestOutputHelper output)
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
    public void The_open_message_carries_every_hotkey_group_in_order_and_the_version()
    {
        using var doc = JsonDocument.Parse(HelpPopup.BuildOpenMessage("0.1.12", null, KeyEngine.SortedHelp()));
        var root = doc.RootElement;
        Assert.Equal("open", root.GetProperty("type").GetString());
        Assert.Equal("0.1.12", root.GetProperty("version").GetString());

        var groups = root.GetProperty("groups").EnumerateArray().ToList();
        Assert.Equal(KeyEngine.HelpCategories, groups.Select(g => g.GetProperty("name").GetString()).ToArray()); // the category order of the old list
        foreach (var g in groups)
        {
            var expected = KeyEngine.SortedHelp().Where(h => h.Category == g.GetProperty("name").GetString()).Select(h => (h.Keys, h.What)).ToList();
            var rows = g.GetProperty("rows").EnumerateArray().Select(r => (r.GetProperty("keys").GetString()!, r.GetProperty("what").GetString()!)).ToList();
            Assert.NotEmpty(rows);
            Assert.Equal(expected, rows); // sorted by shortcut inside the group, every row, text untouched
        }
        Assert.Equal(KeyEngine.Help.Length, groups.Sum(g => g.GetProperty("rows").GetArrayLength()));

        // the message for this run uses this build's version
        using var mine = JsonDocument.Parse(HelpPopup.BuildOpenMessage());
        Assert.Equal(AppVersion.Display, mine.RootElement.GetProperty("version").GetString());
    }

    [Fact]
    public void The_update_row_is_in_the_open_message_only_when_the_updater_has_something_to_say()
    {
        static JsonElement Update(string json, out JsonDocument doc) { doc = JsonDocument.Parse(json); return doc.RootElement.GetProperty("update"); }

        Assert.Equal(JsonValueKind.Null, Update(HelpPopup.BuildOpenMessage("0.1.1", null, KeyEngine.SortedHelp()), out var d0).ValueKind);
        d0.Dispose();

        var ready = Update(HelpPopup.BuildOpenMessage("0.1.1", ("0.1.2 is ready", true), KeyEngine.SortedHelp()), out var d1);
        Assert.Equal("0.1.2 is ready", ready.GetProperty("text").GetString());
        Assert.True(ready.GetProperty("canApply").GetBoolean());
        d1.Dispose();

        var info = Update(HelpPopup.BuildOpenMessage("0.1.1", ("Checking for updates", false), KeyEngine.SortedHelp()), out var d2);
        Assert.False(info.GetProperty("canApply").GetBoolean());
        d2.Dispose();

        // the hook is read each time the menu opens; a hook that throws leaves the row out rather than the menu
        var hook = HelpPopup.UpdateLine;
        try
        {
            HelpPopup.UpdateLine = null;
            Assert.Equal(JsonValueKind.Null, Update(HelpPopup.BuildOpenMessage(), out var d3).ValueKind);
            d3.Dispose();
            HelpPopup.UpdateLine = () => ("0.9.0 is ready", true);
            Assert.Equal("0.9.0 is ready", Update(HelpPopup.BuildOpenMessage(), out var d4).GetProperty("text").GetString());
            d4.Dispose();
            HelpPopup.UpdateLine = () => throw new InvalidOperationException("boom");
            Assert.Equal(JsonValueKind.Null, Update(HelpPopup.BuildOpenMessage(), out var d5).ValueKind);
            d5.Dispose();
        }
        finally { HelpPopup.UpdateLine = hook; }

        // and the page's Update button is a message the host understands
        Assert.Equal("update", HelpPopup.ParseMessage("""{"type":"update"}""")!.Type);
        Assert.Equal("close", HelpPopup.ParseMessage("""{"type":"close"}""")!.Type);
        Assert.Null(HelpPopup.ParseMessage("""{"type":"bogus"}"""));
    }

    [Fact]
    public void The_version_is_shown_without_the_commit_and_a_local_build_is_not_a_public_release()
    {
        Assert.Equal(("0.1.12", true), AppVersion.Parse("0.1.12+1a2b3c4d5e", debugBuild: false));       // main: the numbers, no +commit
        Assert.Equal(("0.1.12", true), AppVersion.Parse("0.1.12", debugBuild: false));
        Assert.Equal(("0.1.12", false), AppVersion.Parse("0.1.12+1a2b3c4d5e", debugBuild: true));       // a Debug build is never a release
        Assert.Equal(("0.1.12-g1a2b3c4d5e", false), AppVersion.Parse("0.1.12-g1a2b3c4d5e", false));      // any other branch: -g<commit>, shown as it is
        Assert.Equal(("0.1.12-g1a2b3c4d5e", false), AppVersion.Parse("0.1.12-g1a2b3c4d5e+1a2b3c4d5e", false));
        Assert.Equal(("0.2.0", true), AppVersion.Parse("v0.2.0", false));
        // off main the informational version has no suffix, only the build's PublicRelease metadata says so: shown as -g<commit>, never a release
        Assert.Equal(("0.1.12-g1a2b3c4", false), AppVersion.Parse("0.1.12+1a2b3c4d5e", false, nonPublicBuild: true));
        Assert.Equal(("dev", false), AppVersion.Parse(null, false));                                      // the build stamped nothing
        Assert.Equal(("dev", false), AppVersion.Parse("", false));
        Assert.Equal(("dev", false), AppVersion.Parse("not a version", false));

        // this very build: what the menu would show
        Assert.False(string.IsNullOrWhiteSpace(AppVersion.Display));
        Assert.DoesNotContain('+', AppVersion.Display);
        output.WriteLine($"informational: {AppVersion.Informational}, shown: {AppVersion.Display}, public: {AppVersion.IsPublicRelease}");
#if DEBUG
        Assert.False(AppVersion.IsPublicRelease);
#endif
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task The_page_renders_off_screen_and_reports_its_cards()
    {
        var root = FindRoot();
        var web = Path.Combine(root, "WindowManager", "web");
        var tmp = Path.Combine(Path.GetTempPath(), "wm-help-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmp);
        Log.Init(tmp, verbose: false);
        HelpPopup.TestOffScreen = true;
        HelpPopup.UserDataFolderOverride = Path.Combine(tmp, "userdata"); // never the real profile
        var hook = HelpPopup.UpdateLine; var clicked = HelpPopup.UpdateClicked;
        var updateClicked = new ManualResetEventSlim();
        try
        {
            HelpPopup.UpdateLine = null;
            var sw = Stopwatch.StartNew();
            HelpPopup.Prewarm(web);
            Assert.False(HelpPopup.IsOpen);

            // the first open waits for the cold start, and never blocks the caller
            Assert.True(HelpPopup.Toggle(IntPtr.Zero));
            Assert.True(HelpPopup.IsOpen);
            await WaitFor(() => HelpPopup.IsShown, 60_000, "the menu to be shown");
            output.WriteLine($"cold prewarm+open to shown: {sw.ElapsedMilliseconds} ms");
            Assert.True(HelpPopup.IsReady);
            Assert.True(IsWindowVisible(HelpPopup.WindowHandle));
            Assert.NotEqual(HelpPopup.WindowHandle, GetForegroundWindow());

            // one card for the version and one per category, in the table editor's own styling
            var page = Eval("""
                JSON.stringify({
                  tiles: document.querySelectorAll('.panel').length + document.querySelectorAll('.section').length,
                  groups: [...document.querySelectorAll('.cols .section h3')].map(h => h.textContent),
                  rows: document.querySelectorAll('.row .w').length,
                  caps: document.querySelectorAll('.row kbd').length,
                  version: document.getElementById('version').textContent,
                  theme: document.documentElement.dataset.theme,
                  font: getComputedStyle(document.body).fontFamily,
                  updHidden: document.getElementById('upd').hidden,
                  bottom: Math.max(...[...document.querySelectorAll('.panel')].map(t => t.getBoundingClientRect().bottom)),
                  right: Math.max(...[...document.querySelectorAll('.panel')].map(t => t.getBoundingClientRect().right)),
                  innerW: innerWidth, innerH: innerHeight, dpr: devicePixelRatio, ow: outerWidth,
                  fontOk: document.fonts.check('600 14px "Plus Jakarta Sans"')
                })
                """);
            output.WriteLine(page.ToString());
            Assert.Equal(1 + KeyEngine.HelpCategories.Length, page.GetProperty("tiles").GetInt32());
            Assert.Equal(KeyEngine.HelpCategories, page.GetProperty("groups").EnumerateArray().Select(h => h.GetString()).ToArray());
            Assert.Equal(KeyEngine.Help.Length, page.GetProperty("rows").GetInt32());
            Assert.True(page.GetProperty("caps").GetInt32() > KeyEngine.Help.Length); // chords are drawn as key caps
            Assert.Equal(AppVersion.Display, page.GetProperty("version").GetString());
            Assert.Equal("light", page.GetProperty("theme").GetString());
            Assert.Contains("Plus Jakarta Sans", page.GetProperty("font").GetString());
            Assert.True(page.GetProperty("fontOk").GetBoolean());
            Assert.True(page.GetProperty("updHidden").GetBoolean()); // the updater said nothing
            Assert.True(page.GetProperty("bottom").GetDouble() + 12 <= page.GetProperty("innerH").GetDouble(), "the cards fit the page's viewport (height)");
            Assert.True(page.GetProperty("right").GetDouble() + 12 <= page.GetProperty("innerW").GetDouble(), "the cards fit the page's viewport (width)");

            // the window is clipped to the cards it reported
            var box = WindowRegionBox(HelpPopup.WindowHandle);
            output.WriteLine($"region box {box.Left},{box.Top} - {box.Right},{box.Bottom}, scale {HelpPopup.LastScale}");
            Assert.True(box.Right > box.Left && box.Bottom > box.Top);
            Assert.True(box.Right <= Math.Ceiling(HelpPopup.LogicalWidth * HelpPopup.LastScale) && box.Bottom <= Math.Ceiling(HelpPopup.LogicalHeight * HelpPopup.LastScale));

            // the screenshot for a human: the page on the paper colour the cards stand on in the table editor
            var shot = Path.Combine(root, "tests", "out", "hotkeys-menu.png");
            Directory.CreateDirectory(Path.GetDirectoryName(shot)!);
            Eval("document.documentElement.style.background = '#f7f4f1'; 1");
            await Task.Delay(150);
            await HelpPopup.CaptureForTest(shot).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(new FileInfo(shot).Length > 10_000);
            output.WriteLine("screenshot: " + shot);

            // both Alts again closes it; closing is a hide, the next open is fast
            Assert.False(HelpPopup.Toggle(IntPtr.Zero));
            await WaitFor(() => !HelpPopup.IsShown && !HelpPopup.IsOpen, 5_000, "the second Toggle to close");
            Assert.False(IsWindowVisible(HelpPopup.WindowHandle));
            Assert.True(HelpPopup.Toggle(IntPtr.Zero));
            await WaitFor(() => HelpPopup.IsShown, 10_000, "the warm open");
            output.WriteLine($"warm open to shown: {HelpPopup.LastOpenToShownMs:0.0} ms");
            Assert.True(HelpPopup.LastOpenToShownMs < 1000, "warm open: " + HelpPopup.LastOpenToShownMs + " ms");

            // clicking elsewhere (deactivate) closes it
            HelpPopup.TestDeactivate();
            await WaitFor(() => !HelpPopup.IsShown && !HelpPopup.IsOpen, 5_000, "the deactivation to close");

            // the updater has something to say: the row, the button, and the button calls the hook (and closes the menu)
            HelpPopup.UpdateLine = () => ("0.9.9 is ready to install", true);
            HelpPopup.UpdateClicked = updateClicked.Set;
            Assert.True(HelpPopup.Toggle(IntPtr.Zero));
            await WaitFor(() => HelpPopup.IsShown, 10_000, "the open with an update");
            var upd = Eval("""JSON.stringify({ hidden: document.getElementById('upd').hidden, text: document.getElementById('upd-text').textContent, button: !document.getElementById('upd-btn').hidden })""");
            output.WriteLine(upd.ToString());
            Assert.False(upd.GetProperty("hidden").GetBoolean());
            Assert.Equal("0.9.9 is ready to install", upd.GetProperty("text").GetString());
            Assert.True(upd.GetProperty("button").GetBoolean());
            Eval("document.getElementById('upd-btn').click(); 1");
            Assert.True(updateClicked.Wait(10_000), "UpdateClicked was called");
            await WaitFor(() => !HelpPopup.IsShown && !HelpPopup.IsOpen, 5_000, "the Update click to close the menu");

            // Esc in the page closes it
            HelpPopup.UpdateLine = null;
            Assert.True(HelpPopup.Toggle(IntPtr.Zero));
            await WaitFor(() => HelpPopup.IsShown, 10_000, "the open for Esc");
            Eval("document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true })); 1");
            await WaitFor(() => !HelpPopup.IsShown && !HelpPopup.IsOpen, 5_000, "Esc to close");
        }
        finally
        {
            HelpPopup.Shutdown();
            HelpPopup.TestOffScreen = false;
            HelpPopup.UserDataFolderOverride = null;
            HelpPopup.UpdateLine = hook; HelpPopup.UpdateClicked = clicked;
            try { output.WriteLine(File.ReadAllText(Path.Combine(tmp, "wm.log"))); } catch (IOException) { }
            await Task.Delay(500); // let the browser processes of the temp profile exit before the folder goes
            try { Directory.Delete(tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // The repository root: the folder above WindowManager\web (the test runs from bin\..., the page under test is the source one).
    static string FindRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "WindowManager", "web", "hotkeys.html"))) return d.FullName;
        throw new DirectoryNotFoundException("WindowManager\\web\\hotkeys.html not found above " + AppContext.BaseDirectory);
    }

    // The page's script result: ExecuteScriptAsync returns JSON, and a JSON.stringify result arrives as a JSON string of that JSON.
    static JsonElement Eval(string js)
    {
        var r = HelpPopup.EvalForTest(js) ?? throw new TimeoutException("the page did not answer");
        using var outer = JsonDocument.Parse(r);
        if (outer.RootElement.ValueKind != JsonValueKind.String) return outer.RootElement.Clone();
        using var inner = JsonDocument.Parse(outer.RootElement.GetString()!);
        return inner.RootElement.Clone();
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
