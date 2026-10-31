using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using static WindowManager.Native;

namespace WindowManager;

// Runs a macro against the live manager inside the sandbox and records readings. It never decides how to fix
// anything: it presses keys through the real key path, watches every window every couple of milliseconds, and
// reports what it saw (SandboxReport.cs turns that into report.md / readings.json).
sealed partial class SandboxRunner
{
    readonly string macroPath, outDir, fixtureExe;
    readonly IntPtr msgWnd;
    readonly double tpm = Stopwatch.Frequency / 1000.0;   // ticks per millisecond
    readonly DateTime startedAt = DateTime.Now;
    readonly Stopwatch wall = Stopwatch.StartNew();

    // Tunable from the macro with `set`. Defaults are the quality bar: a tile should react within 150ms and settle within 900ms,
    // nothing may sit on the loop for 100ms, and no in-between state may be on screen for a frame (12ms).
    readonly Dictionary<string, double> opt = new(StringComparer.OrdinalIgnoreCase)
    {
        ["quietMs"] = 300, ["timeoutMs"] = 12000, ["keyGapMs"] = 10, ["glitchMs"] = 12, ["loopSlowMs"] = 100,
        ["firstChangeMs"] = 150, ["settleMs"] = 900, ["keyQueueMs"] = 50, ["overshootPx"] = 24, ["hintsMs"] = 800,
        ["spawnMs"] = 10000, ["edgeMs"] = 20000, ["appFrameMs"] = 34,
    };

    public SandboxRunner(string macro, string outDir, string fixtureExe, IntPtr msgWnd)
    {
        macroPath = macro; this.outDir = outDir; this.fixtureExe = fixtureExe; this.msgWnd = msgWnd;
    }

    long Now => Stopwatch.GetTimestamp();
    double Ms(long ticks) => ticks / tpm;
    string LogPath => Path.Combine(outDir, "wm.log");

    // ------------------------------------------------------------------ run

    public void Run()
    {
        try
        {
            Directory.CreateDirectory(outDir);
            timeBeginPeriod(1);
            StartBackgroundThreads();
            var cmds = MacroParser.Expand(MacroParser.Parse(File.ReadAllText(macroPath)));
            Note($"macro {macroPath}: {cmds.Count} commands");
            Preflight();
            if (caps.Abort is { } why) { Add("FAIL", "environment", why, "preflight", ""); }
            else Execute(cmds);
        }
        catch (Exception e) { Add("FAIL", "runner-crash", e.ToString(), curTest, curStep); }
        finally
        {
            try { EndTest(); } catch { }
            try { WriteProgress("macro finished, shutting the manager down"); } catch { }
            Program.PostLoop(() => PostMessage(msgWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero));   // Quit: the manager restores everything
        }
    }

    // Called after the manager's loop ended and it restored every window: what did shutdown leave behind?
    public void Finish()
    {
        try
        {
            running = false;
            AfterShutdown();
            foreach (var f in fx.Values) { try { if (!f.Proc.HasExited) f.Proc.Kill(true); } catch { } }
            WriteReports();
        }
        catch (Exception e) { File.AppendAllText(Path.Combine(outDir, "runner-errors.txt"), e + "\n"); }
    }

    // ------------------------------------------------------------------ background threads

    volatile bool running = true;
    readonly object sg = new();
    readonly List<Sample> samples = [];
    readonly HashSet<long> tracked = [];
    readonly List<(long Ticks, long Hwnd)> fgChanges = [];
    readonly List<(long Ticks, double Ms)> loopLat = [];

    void StartBackgroundThreads()
    {
        new Thread(Sampler) { IsBackground = true, Name = "sandbox-sampler", Priority = ThreadPriority.AboveNormal }.Start();
        new Thread(LoopProbe) { IsBackground = true, Name = "sandbox-loop-probe" }.Start();
    }

    // Every window's rect and style bits, every ~2ms, kept only when something changed: the trajectory of a transition.
    void Sampler()
    {
        var last = new Dictionary<long, (Rect, WinFlag)>();
        long lastFg = 0;
        while (running)
        {
            long[] hs; lock (sg) hs = [.. tracked];
            foreach (var h in hs)
            {
                if (!IsWindow((IntPtr)h)) continue;
                if (!GetWindowRect((IntPtr)h, out var r)) continue;
                var f = FlagsOf((IntPtr)h);
                if (last.TryGetValue(h, out var old) && old.Item1 == r && old.Item2 == f) continue;
                last[h] = (r, f);
                lock (sg) samples.Add(new Sample(Now, h, r, f));
            }
            var fg = (long)Desk.RawForeground();
            if (fg != lastFg) { lastFg = fg; lock (sg) fgChanges.Add((Now, fg)); }
            Thread.Sleep(2);
        }
    }

    static WinFlag FlagsOf(IntPtr h)
    {
        var s = (uint)GetWindowLong(h, GWL_STYLE);
        var f = WinFlag.None;
        if (IsWindowVisible(h)) f |= WinFlag.Visible;
        if (IsIconic(h)) f |= WinFlag.Iconic;
        if (IsZoomed(h)) f |= WinFlag.Zoomed;
        if ((s & Chrome.WS_CAPTION) == Chrome.WS_CAPTION) f |= WinFlag.Caption;
        if ((s & Chrome.WS_THICKFRAME) != 0) f |= WinFlag.Frame;
        return f;
    }

    // How long a trivial request takes to get onto the loop thread and run: the manager's responsiveness, measured all the time.
    void LoopProbe()
    {
        while (running)
        {
            var t = Now;
            Program.PostLoop(() => { var lat = Ms(Now - t); lock (sg) loopLat.Add((t, lat)); });
            Thread.Sleep(20);
        }
    }

    // ------------------------------------------------------------------ fixtures

    sealed class Fx
    {
        public string Name = "", Mode = ""; public Process Proc = null!; public IntPtr Hwnd; public Rect Start;
        public int Spawns;
    }
    readonly Dictionary<string, Fx> fx = new(StringComparer.OrdinalIgnoreCase);

    static string TitleOf(IntPtr h)
    {
        var sb = new StringBuilder(256);
        return SendMessageTimeout(h, 0x000D /*WM_GETTEXT*/, (IntPtr)256, sb, 0x2 /*SMTO_ABORTIFHUNG*/, 200, out _) != IntPtr.Zero ? sb.ToString() : "";
    }

    static readonly Regex TitleRx = new(@"fs=(\d) f11=(\d+) clicks=(\d+) keys=(\d+)", RegexOptions.Compiled);
    (bool? Fs, int F11, int Clicks, int Keys) FixtureState(Fx f)
    {
        var m = TitleRx.Match(TitleOf(f.Hwnd));
        return m.Success ? (m.Groups[1].Value == "1", int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value), int.Parse(m.Groups[4].Value)) : (null, 0, 0, 0);
    }

    Fx Spawn(string name, string mode, Rect? at = null, bool activate = true)
    {
        if (fx.TryGetValue(name, out var old)) Kill(old);
        var r = at ?? new Rect(120 + fx.Count * 40, 90 + fx.Count * 30, 120 + fx.Count * 40 + 820, 90 + fx.Count * 30 + 580);
        var psi = new ProcessStartInfo(ExeFor(mode), $"--name {name} --mode {mode} --x {r.Left} --y {r.Top} --w {r.Width} --h {r.Height}") { UseShellExecute = false };
        var p = Process.Start(psi) ?? throw new InvalidOperationException("fixture did not start");
        var f = new Fx { Name = name, Mode = mode, Proc = p, Start = r, Spawns = (old?.Spawns ?? 0) + 1 };
        var end = Now + (long)(opt["spawnMs"] * tpm);
        while (Now < end && f.Hwnd == IntPtr.Zero)
        {
            EnumWindows((h, _) =>
            {
                GetWindowThreadProcessId(h, out var pid);
                if (pid == p.Id && IsWindowVisible(h) && TitleOf(h).StartsWith($"WmFixture:{name} ")) { f.Hwnd = h; return false; }
                return true;
            }, IntPtr.Zero);
            if (f.Hwnd == IntPtr.Zero) Thread.Sleep(20);
        }
        if (f.Hwnd == IntPtr.Zero) throw new InvalidOperationException($"fixture {name} showed no window within {opt["spawnMs"]}ms");
        fx[name] = f;
        lock (sg) tracked.Add((long)f.Hwnd);
        // Wait for the manager to notice it (a show event, or the pump), like a real app that has just opened.
        var seen = Now + (long)(3000 * tpm);
        while (Now < seen && !Loop(Program.TakeSnapshot).Windows.Any(w => w.Hwnd == (long)f.Hwnd)) Thread.Sleep(15);
        if (activate) ActivateWindow(f);
        return f;
    }

    // The manager remembers which programs can do a fullscreen by process NAME, so every mode runs as its own program
    // (a renamed copy of the same exe: the app dll it loads is named inside it).
    string ExeFor(string mode)
    {
        var path = Path.Combine(Path.GetDirectoryName(fixtureExe)!, $"WmFx-{mode}.exe");
        if (!File.Exists(path)) File.Copy(fixtureExe, path);
        return path;
    }

    void Kill(Fx f)
    {
        try { if (!f.Proc.HasExited) f.Proc.Kill(true); f.Proc.WaitForExit(2000); } catch { }
        lock (sg) tracked.Remove((long)f.Hwnd);
    }

    // What clicking it in the taskbar / Alt+Tab does: un-minimize, then it is the foreground window.
    void ActivateWindow(Fx f)
    {
        if (IsIconic(f.Hwnd)) ShowWindow(f.Hwnd, SW_RESTORE);
        Desk.SetForeground(f.Hwnd);
        SetWindowPos(f.Hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE); SetWindowPos(f.Hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);   // and on top, so what the cursor hits is what is focused
    }

    T Loop<T>(Func<T> f) => Program.OnLoop(f, (int)opt["timeoutMs"]);

    // ------------------------------------------------------------------ keys

    readonly HashSet<int> physical = [];     // keys the macro is holding down right now

    void KeyEvent(int vk, bool down)
    {
        if (down) physical.Add(vk);
        else physical.Remove(vk);
        Program.SandboxKey(vk, down);
        Thread.Sleep((int)opt["keyGapMs"]);
    }

    // Press in order, release in reverse: a real chord. A modifier-only chord ("Win+Alt") is therefore a tap.
    void KeyChord(string chord)
    {
        var vks = Chord.Parse(chord);
        foreach (var vk in vks) KeyEvent(vk, true);
        foreach (var vk in vks.Reverse()) KeyEvent(vk, false);
    }

    // ------------------------------------------------------------------ the single environment check

    sealed class Caps
    {
        public bool RealWinEvents, SyntheticWinEvents, ForegroundChain, Dll, DwmFrame, Uia, F11Delivery, Emission;
        public string? Abort;
        public string Monitor = "", Work = "", Detail = "";
        public double UiaScanMs, ActivateToLieMs, SleepResolutionMs;
        public int UiaTargets;
    }
    readonly Caps caps = new();

    // The ONE place the environment is examined: everything the rest of the run depends on, found out in a single pass with a
    // single throwaway window. Groups that need something missing are skipped with the reason; nothing is re-probed later.
    void Preflight()
    {
        var detail = new List<string>();
        try
        {
            var mon = Frames.AllMonitors().FirstOrDefault(m => m.Primary) ?? throw new InvalidOperationException("no monitor on the hidden desktop");
            caps.Monitor = mon.Bounds.ToString(); caps.Work = mon.Work.ToString();
            var sw = Stopwatch.StartNew(); for (var i = 0; i < 20; i++) Thread.Sleep(1);
            caps.SleepResolutionMs = sw.Elapsed.TotalMilliseconds / 20;

            var p = Spawn("P", "std");
            var t0 = Now;
            var deadline = Now + (long)(8000 * tpm);
            MgrWinView? w = null;
            while (Now < deadline)
            {
                var s = Loop(Program.TakeSnapshot);
                var m = s.Windows.FirstOrDefault(x => x.Hwnd == (long)p.Hwnd);
                w = m is null ? null : new MgrWinView(m.Lie, m.NotCapable, m.Probing);
                if (w is { Lie: true } && s.NotQuiet.Length == 0) break;
                Thread.Sleep(30);
            }
            caps.ForegroundChain = Loop(Program.TakeSnapshot).Foreground == (long)p.Hwnd;
            caps.ActivateToLieMs = Ms(Now - t0);
            var (show, loc, destroy, fg, synth) = Program.EventCounts;
            caps.RealWinEvents = show > 0 && loc > 0;
            caps.SyntheticWinEvents = synth > 0;
            GetWindowRect(p.Hwnd, out var rect);
            var st = FixtureState(p);
            caps.Dll = w is { Lie: true } && st.Fs == true;
            caps.F11Delivery = st.F11 >= 1;
            caps.DwmFrame = Frames.FrameBounds(p.Hwnd) is { } fb && fb.Width > 0;
            caps.Emission = Desk.EmitCount > 0;
            detail.Add($"entered fullscreen-in-slot in {caps.ActivateToLieMs:F0}ms, window now {rect}, app says fs={st.Fs}, F11s received={st.F11}");
            detail.Add($"window events from Windows: show={show} location={loc} destroy={destroy} foreground={fg}; from the pump: {synth}");

            // One check, but a tolerant one: UI Automation can answer "unexpected HRESULT" once when it is also busy with the manager's own startup
            // prefetch; a failure here would silently skip every hint test, so the same single check is attempted up to three times.
            for (var attempt = 1; attempt <= 3 && !caps.Uia; attempt++)
            {
                try
                {
                    var scan = Stopwatch.StartNew();
                    var targets = Hints.Scan([p.Hwnd], log: false);
                    caps.UiaScanMs = scan.Elapsed.TotalMilliseconds; caps.UiaTargets = targets.Count;
                    caps.Uia = targets.Count > 0;
                }
                catch (Exception e) { detail.Add($"UI Automation scan attempt {attempt} failed: {e.Message}"); }
                if (!caps.Uia) Thread.Sleep(400);
            }
            Kill(p); fx.Remove("P");
        }
        catch (Exception e) { caps.Abort = $"environment check failed: {e.Message}"; detail.Add(e.ToString()); }
        caps.Detail = string.Join("\n", detail);
        if (caps.Abort is null && !caps.Dll) caps.Abort = "the monitor lie did not engage in the sandbox (the fixture never reached fullscreen-in-slot): nothing else can be judged";
        if (caps.Abort is null && !caps.F11Delivery) caps.Abort = "posted F11 never reached the fixture";
        Note($"preflight: dll={caps.Dll} winevents(real={caps.RealWinEvents} pump={caps.SyntheticWinEvents}) uia={caps.Uia} dwm={caps.DwmFrame}");
    }

    sealed record MgrWinView(bool Lie, bool NotCapable, bool Probing);

    bool Has(string cap) => cap.ToLowerInvariant() switch
    {
        "uia" => caps.Uia, "dwmframe" => caps.DwmFrame, "dll" => caps.Dll, "winevents" => caps.RealWinEvents || caps.SyntheticWinEvents,
        "edge" => File.Exists(EdgePath), _ => true,
    };

    static string EdgePath => new[] { @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", @"C:\Program Files\Microsoft\Edge\Application\msedge.exe" }
        .FirstOrDefault(File.Exists) ?? "";

    // ------------------------------------------------------------------ Win32 helpers local to the runner

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, StringBuilder l, uint flags, uint timeout, out IntPtr result);
    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
}
