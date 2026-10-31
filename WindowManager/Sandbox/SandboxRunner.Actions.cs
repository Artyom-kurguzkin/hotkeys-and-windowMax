using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using static WindowManager.Native;

namespace WindowManager;

sealed partial class SandboxRunner
{
    // ------------------------------------------------------------------ findings

    sealed record Finding(string Sev, string Rule, string Message, string Test, string Step, string Evidence);   // Sev: FAIL | UX | INFO
    sealed class TestRec { public string Group = "", Name = ""; public int Checks, Fails, Ux; public string? Skipped; public double Ms; public readonly List<string> Rules = []; }
    sealed class ActionRec
    {
        public string Group = "", Test = "", Label = "";
        public double FirstChangeMs, SettleMs, KeyQueueMs, LoopMaxMs, LoopP95Ms; public int F11, States, Transients; public int AppFrames; public bool Timeout;
    }

    readonly List<Finding> findings = [];
    readonly List<TestRec> tests = [];
    readonly List<ActionRec> actionRecs = [];
    string curGroup = "", curTest = "", curStep = "";
    TestRec? curRec;
    bool quiet;                       // normalisation and setup: observed, never judged
    readonly List<Regex> allowLog = [];
    string checkSev = "FAIL";         // "UX" while a ux-expect runs: a quality bar the user asked for, not a documented defect
    readonly Dictionary<string, string> notes = [];
    readonly Dictionary<string, int> clickBase = new(StringComparer.OrdinalIgnoreCase);   // clicks a fixture had already received when the test began

    void Add(string sev, string rule, string message, string test, string step, string evidence = "")
    {
        if (quiet && sev != "INFO") return;
        lock (findings)
        {
            findings.Add(new Finding(sev, rule, message, test, step, evidence));
            if (curRec is { } t) { if (sev == "FAIL") t.Fails++; else if (sev == "UX") t.Ux++; if (sev != "INFO" && !t.Rules.Contains(rule)) t.Rules.Add(rule); }
        }
    }

    void Check(bool ok, string rule, string message, string evidence = "")
    {
        if (curRec is { } t) t.Checks++;
        if (!ok) Add(checkSev, rule, message, curTest, curStep, evidence);
    }

    void Note(string text)
    {
        try { File.AppendAllText(Path.Combine(outDir, "progress.log"), $"{wall.Elapsed.TotalSeconds,7:F1}s {text}\n"); } catch { }
    }

    void WriteProgress(string text) => Note(text);

    // ------------------------------------------------------------------ the macro

    string? skipRest;                 // set by `require`: the rest of this test is skipped, with this reason
    bool Active(Cmd c) => skipRest is null || c.Verb is "test" or "group" or "endtest";

    void Execute(List<Cmd> cmds)
    {
        foreach (var c in cmds)
        {
            curStep = $"line {c.Line}: {c.Raw}";
            if (!Active(c)) continue;
            try { Verb(c); }
            catch (Exception e)
            {
                Add("FAIL", e is TimeoutException ? "manager-unresponsive" : "macro-error", $"{c.Raw}: {e.Message}", curTest, curStep, e.ToString());
                cur = null;   // abandon a half-run action
            }
        }
    }

    void Verb(Cmd c)
    {
        // `ux-expect...` is any expectation counted against the quality bar (glitch, lag, redundant work) instead of as a defect.
        if (c.Verb.StartsWith("ux-")) { checkSev = "UX"; try { Verb(c with { Verb = c.Verb[3..] }); } finally { checkSev = "FAIL"; } return; }
        var a = c.Args;
        switch (c.Verb)
        {
            case "set": opt[a[0]] = double.Parse(a[1], System.Globalization.CultureInfo.InvariantCulture); break;
            case "note": Note(string.Join(' ', a)); break;
            case "group": EndTest(); curGroup = string.Join(' ', a); Note($"== {curGroup}"); break;
            case "test": EndTest(); StartTest(string.Join(' ', a)); break;
            case "endtest": EndTest(); break;
            case "require": foreach (var cap in a) if (!Has(cap)) { skipRest = $"needs {cap}, which the environment check found unavailable"; break; } break;
            case "allow-log": allowLog.Add(new Regex(string.Join(' ', a), RegexOptions.IgnoreCase)); break;

            // fixtures and windows
            case "spawn": Spawn(a[0], a[1], a.Length >= 6 ? new Rect(int.Parse(a[2]), int.Parse(a[3]), int.Parse(a[2]) + int.Parse(a[4]), int.Parse(a[3]) + int.Parse(a[5])) : null); actorName = a[0]; if (cur is null) SettleSilently(); break;
            case "spawnbg": Spawn(a[0], a[1], a.Length >= 6 ? new Rect(int.Parse(a[2]), int.Parse(a[3]), int.Parse(a[2]) + int.Parse(a[4]), int.Parse(a[3]) + int.Parse(a[5])) : null, activate: false); if (cur is null) SettleSilently(); break;
            case "kill": Begin($"kill {a[0]}", expected: []); Kill(fx[a[0]]); break;
            case "close": Begin($"close {a[0]}", expected: [fx[a[0]].Hwnd]); PostMessage(fx[a[0]].Hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero); break;
            case "actor": actorName = a[0]; break;
            case "desktop-away": { var f = fx[a[0]]; Begin($"desktop-away {a[0]}", expected: [f.Hwnd]); Desk.MarkAway(f.Hwnd, true); break; }   // the user switched to another virtual desktop
            case "desktop-back": { var f = fx[a[0]]; Begin($"desktop-back {a[0]}", expected: [f.Hwnd]); Desk.MarkAway(f.Hwnd, false); break; }
            case "activate": { var f = fx[a[0]]; actorName = a[0]; Begin($"activate {a[0]}", expected: [f.Hwnd]); ActivateWindow(f); break; }
            case "place": { var f = fx[a[0]]; Begin($"place {a[0]}", expected: [f.Hwnd]); SetWindowPos(f.Hwnd, IntPtr.Zero, int.Parse(a[1]), int.Parse(a[2]), int.Parse(a[3]), int.Parse(a[4]), SWP_NOZORDER | SWP_NOACTIVATE); break; }
            case "zone": { var f = fx[a[0]]; Begin($"zone {a[0]}", expected: [f.Hwnd]); var wp = new WINDOWPLACEMENT { length = System.Runtime.InteropServices.Marshal.SizeOf<WINDOWPLACEMENT>() }; GetWindowPlacement(f.Hwnd, ref wp); wp.showCmd = SW_SHOWNORMAL; wp.flags = 0; wp.rcNormal = new Rect(int.Parse(a[1]), int.Parse(a[2]), int.Parse(a[1]) + int.Parse(a[3]), int.Parse(a[2]) + int.Parse(a[4])); SetWindowPlacement(f.Hwnd, ref wp);
                // Windows places the VISIBLE frame on the zone (the invisible borders hang outside it); do the same.
                Thread.Sleep(60); GetWindowRect(f.Hwnd, out var zw); if (Frames.FrameBounds(f.Hwnd) is { } zf) { wp.rcNormal = LieLogic.AlignVisible(zw, zf, new Rect(int.Parse(a[1]), int.Parse(a[2]), int.Parse(a[1]) + int.Parse(a[3]), int.Parse(a[2]) + int.Parse(a[4]))); SetWindowPlacement(f.Hwnd, ref wp); }
                break; }   // what Windows does for a Snap Layouts pick: un-maximize and place, in one step
            case "maximize": { var f = fx[a[0]]; Begin($"maximize {a[0]}", expected: [f.Hwnd]); ShowExternally(f, SW_MAXIMIZE); break; }
            case "minimize": { var f = fx[a[0]]; Begin($"minimize {a[0]}", expected: [f.Hwnd]); ShowWindow(f.Hwnd, SW_MINIMIZE); break; }
            case "restore": { var f = fx[a[0]]; Begin($"restore {a[0]}", expected: [f.Hwnd]); ShowExternally(f, SW_RESTORE); break; }
            case "appf11": { var f = fx[a[0]]; Begin($"appf11 {a[0]}", expected: [f.Hwnd]); UserF11(f); break; }
            case "fixture": { var f = fx[a[0]]; Begin($"fixture {a[0]} {a[1]}", expected: [f.Hwnd]); PostMessage(f.Hwnd, 0x8001, a[1] == "crash" ? 2 : 1, a.Length > 2 ? int.Parse(a[2]) : 0); break; }

            // input
            case "key": Begin($"key {a[0]}", chord: a[0]); KeyChord(a[0]); break;
            case "down": Begin($"down {a[0]}"); foreach (var vk in Chord.Parse(a[0])) KeyEvent(vk, true); break;
            case "up": Begin($"up {a[0]}"); foreach (var vk in Chord.Parse(a[0]).Reverse()) KeyEvent(vk, false); break;
            case "repeat": { Begin($"repeat {a[0]}x {a[2]}", chord: a[2]); for (var i = 0; i < int.Parse(a[0]); i++) { KeyChord(a[2]); Thread.Sleep(int.Parse(a[1])); } break; }
            case "stress": { Begin($"stress {a[0]}x {string.Join(' ', a.Skip(2))}"); for (var i = 0; i < int.Parse(a[0]); i++) { foreach (var ch in a.Skip(2)) KeyChord(ch); Thread.Sleep(int.Parse(a[1])); } break; }
            case "type": Begin($"type {a[0]}"); foreach (var ch in a[0]) KeyChord(ch.ToString()); break;
            case "sleep": Thread.Sleep(int.Parse(a[0])); break;
            case "cursor": Begin($"cursor {a[0]},{a[1]}"); Desk.SetCursor(int.Parse(a[0]), int.Parse(a[1])); break;
            case "cursor-on": { var f = fx[a[0]]; GetWindowRect(f.Hwnd, out var r); var (cx, cy) = r.Center; actorName = a[0]; Begin($"cursor on {a[0]}", expected: [f.Hwnd]); Desk.SetCursor(cx, cy); Thread.Sleep(900); break; }   // one hover tick is 700ms
            case "hint-show": HintShow(); break;
            case "settle": Settle(); break;

            // generators
            case "act": Act(a[0]); break;
            case "normalize": Normalize(a[0], a.Length > 1 && a[1] == "bystander"); break;
            case "singles": Singles(a[0], a[1].Split(','), a.Length > 2 && a[2] == "bystander"); break;
            case "pairs": Pairs(a[0], a[1].Split(','), a.Length > 2 && a[2] == "bystander"); break;
            case "walk": Walk(a[0], int.Parse(a[1]), int.Parse(a[2]), a[3].Split(','), a.Length > 4 && a[4] == "bystander"); break;

            // expectations
            case "expect": ExpectWindow(a); break;
            case "expect-emit-latency": ExpectEmitLatency(a[0], a[1], double.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture)); break;
            case "expect-cursor-in": { GetWindowRect(fx[a[0]].Hwnd, out var cr); Desk.GetCursor(out var cp); Check(cr.Contains(cp.X, cp.Y), "cursor-not-centred", $"cursor is at {cp.X},{cp.Y}, outside {a[0]} {cr}"); break; }
            case "idle": Begin("idle", expected: []); Thread.Sleep(int.Parse(a[0])); Settle(); break;
            case "quit-and-finish": QuitAndFinish(); break;
            case "expect-tap": ExpectTap(Chord.One(a[0]), a.Length > 1 ? int.Parse(a[1]) : -1); break;
            case "expect-wheel": ExpectWheel(a[0], int.Parse(a[1])); break;
            case "expect-click": ExpectClick(); break;
            case "expect-no-output": ExpectNoOutput(); break;
            case "expect-clean": ExpectCleanKeys(); break;
            case "expect-log": { var hit = LogLines(last).Any(l => Regex.IsMatch(l, string.Join(' ', a), RegexOptions.IgnoreCase)); Check(hit, "log-missing", $"no log line matching /{string.Join(' ', a)}/ during the action"); break; }
            case "expect-nolog": { var bad = LogLines(last).FirstOrDefault(l => Regex.IsMatch(l, string.Join(' ', a), RegexOptions.IgnoreCase)); Check(bad is null, "log-unexpected", $"log line matching /{string.Join(' ', a)}/: {bad}"); break; }
            case "expect-hints": ExpectHints(a); break;
            case "expect-clicks": { var n = FixtureState(fx[a[0]]).Clicks - clickBase.GetValueOrDefault(a[0]); Check(n == int.Parse(a[1]), "clicks", $"{a[0]} received {n} click(s) during this test, expected {a[1]}"); break; }
            case "expect-unchanged": { var f = fx[a[0]]; var tr = Traj(f, last); Check(!tr.Changed, "unchanged", $"{a[0]} changed: {Describe(tr)}"); break; }
            case "expect-f11": ExpectCount("f11", Emits(last).Count(e => e.Kind == "f11"), a[0]); break;
            case "expect-metric": ExpectMetric(a[0], a[1], double.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture)); break;
            case "snapshot": { var w = Facts(); Add("INFO", "snapshot", a.Length > 0 ? string.Join(' ', a) : "snapshot", curTest, curStep, Describe(w)); break; }
            default: throw new FormatException($"unknown verb '{c.Verb}'");
        }
    }

    // ------------------------------------------------------------------ tests

    DateTime testStart;
    void StartTest(string name)
    {
        curTest = name; skipRest = null; allowLog.Clear(); testStart = DateTime.Now; quiet = false;
        Desk.ClearForeground();   // every test starts with nothing focused, as if the user had just sat down
        foreach (var other in fx.Values.Where(f => f.Name != "A").ToList()) { Kill(other); fx.Remove(other.Name); }   // leftovers of earlier tests would be judged again in this one
        clickBase.Clear(); foreach (var f in fx.Values) clickBase[f.Name] = FixtureState(f).Clicks;
        curRec = new TestRec { Group = curGroup, Name = name };
        lock (findings) tests.Add(curRec);
        cur = null; last = null;
        Note($"-- {name}");
    }

    void EndTest()
    {
        if (cur is not null) { try { Settle(); } catch (Exception e) { Add("FAIL", "macro-error", $"final settle: {e.Message}", curTest, curStep); } }
        if (curRec is { } t) { t.Ms = (DateTime.Now - testStart).TotalMilliseconds; t.Skipped = skipRest; }
        curRec = null; skipRest = null; quiet = false;
    }

    // ------------------------------------------------------------------ actions

    sealed class Ctx
    {
        public long T0, T1; public int EmitIdx, KeyDispatchIdx; public long LogOff; public string Label = "";
        public List<string> Chords = [];
        public bool HasKeys; public bool ExpectFocus;   // ExpectFocus: this action is meant to give the actor the focus
        public HashSet<int> PhysicalAtStart = [];
        public WorldFacts Before = null!;
        public HashSet<long> Expected = [];
        public string? Chord => Chords.Count == 1 ? Chords[0] : null;
        public long ActorHwnd;
        public string Evidence = "";
    }
    Ctx? cur, last;
    string actorName = "A";
    Fx? Actor => fx.TryGetValue(actorName, out var f) ? f : null;

    // The first thing an action does: remember where everything is, so Settle can say what changed and why.
    void Begin(string label, string? chord = null, IEnumerable<IntPtr>? expected = null)
    {
        if (cur is null)
        {
            cur = new Ctx
            {
                T0 = 0, EmitIdx = Desk.EmitCount, KeyDispatchIdx = Program.KeyDispatches.Count, LogOff = LogLength(), Label = label,
                PhysicalAtStart = [.. physical], Before = Facts(),
            };
            cur.T0 = Now;   // the action starts here, after the (slow) gathering of facts above
        }
        else cur.Label += " + " + label;
        if (chord is not null) { cur.Chords.Add(chord); cur.HasKeys = true; }
        if (label.StartsWith("activate") || label.StartsWith("other") || label.StartsWith("back") || label.StartsWith("hover") || label.StartsWith("cursor on")) cur.ExpectFocus = true;
        if (label.StartsWith("key") || label.StartsWith("down") || label.StartsWith("up") || label.StartsWith("type") || label.StartsWith("repeat") || label.StartsWith("stress")) cur.HasKeys = true;
        // Expected to change: the explicit targets, or by default the window being operated on.
        if (expected is not null) foreach (var h in expected) cur.Expected.Add((long)h);
        else if (Actor is { } f) cur.Expected.Add((long)f.Hwnd);
        cur.ActorHwnd = Actor is { } af ? (long)af.Hwnd : 0;
    }

    long LogLength() { try { return new FileInfo(LogPath).Length; } catch { return 0; } }

    IEnumerable<string> LogLines(Ctx? ctx)
    {
        if (ctx is null) yield break;
        string text;
        try
        {
            using var fs = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (ctx.LogOff > fs.Length) yield break;
            fs.Seek(ctx.LogOff, SeekOrigin.Begin);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            text = sr.ReadToEnd();
        }
        catch (IOException) { yield break; }
        foreach (var l in text.Split('\n')) if (l.Length > 0) yield return l.TrimEnd('\r');
    }

    Desk.Emit[] Emits(Ctx? ctx) => ctx is null ? [] : Desk.EmittedSince(ctx.EmitIdx);

    long LastActivity(Ctx ctx)
    {
        long t = ctx.T0;
        lock (sg)
        {
            if (samples.Count > 0) t = Math.Max(t, samples[^1].Ticks);
            if (fgChanges.Count > 0) t = Math.Max(t, fgChanges[^1].Ticks);
        }
        var em = Desk.EmittedSince(ctx.EmitIdx);
        if (em.Length > 0) t = Math.Max(t, em[^1].Ticks);
        return t;
    }

    void SettleSilently()
    {
        var was = quiet; quiet = true;
        try { Begin("setup"); Settle(analyze: false); } finally { quiet = was; }
    }

    // Wait until the manager says it is idle AND nothing has moved or been typed for quietMs, then judge the action.
    void Settle(bool analyze = true)
    {
        var ctx = cur ?? new Ctx { T0 = Now, EmitIdx = Desk.EmitCount, LogOff = LogLength(), Label = "(idle)", Before = Facts() };
        var timeout = Now + (long)(opt["timeoutMs"] * tpm);
        Program.MgrSnap snap; var timedOut = false;
        while (true)
        {
            snap = Loop(Program.TakeSnapshot);
            var busy = snap.NotQuiet.Where(r => !(opt.GetValueOrDefault("ignoreZone") > 0 && r.StartsWith("waiting for a Snap"))).ToArray();
            if (busy.Length == 0 && Ms(Now - LastActivity(ctx)) >= opt["quietMs"]) break;
            if (Now > timeout) { timedOut = true; break; }
            Thread.Sleep(15);
        }
        ctx.T1 = Now;
        cur = null; last = ctx;
        if (analyze) Analyze(ctx, snap, timedOut);
    }

    // ------------------------------------------------------------------ facts

    string Describe(Trajectory tr) =>
        string.Join(" -> ", tr.States.Take(10).Select(s => $"{s.Rect}{(s.Flags.HasFlag(WinFlag.Iconic) ? "[min]" : "")}{(s.Flags.HasFlag(WinFlag.Caption) ? "[chrome]" : "")}@{s.StartMs:F0}ms/{s.DurMs:F0}ms")) + (tr.States.Count > 10 ? " ..." : "");

    string Describe(WorldFacts w)
    {
        var z = new Dictionary<long, int>(); var zi = 0;   // front to back: which window is ON TOP matters for hover and for what the user sees
        EnumWindows((h, _) => { z[(long)h] = zi++; return true; }, IntPtr.Zero);
        return DescribeWith(w, z);
    }

    string DescribeWith(WorldFacts w, Dictionary<long, int> z) =>
        string.Join("\n", w.Windows.Select(f => $"{f.Name}({f.Mode}) z={z.GetValueOrDefault(f.Hwnd, -1)} rect={f.Rect} frame={f.Frame?.ToString() ?? "-"} lie={f.Lie} slot={f.Slot?.ToString() ?? "-"} appFs={f.AppFullscreen?.ToString() ?? "?"} iconic={f.Iconic} zoomed={f.Zoomed} revealed={f.Revealed} notCapable={f.NotCapable}")) +
        $"\nforeground=0x{w.Foreground:X} actor=0x{w.Actor:X} monitor={w.Monitor} work={w.Work}";

    WorldFacts Facts(Program.MgrSnap? snap = null)
    {
        snap ??= Loop(Program.TakeSnapshot);
        var mon = Frames.AllMonitors().FirstOrDefault(m => m.Primary) ?? Frames.AllMonitors().First();
        var wins = new List<WinFacts>();
        foreach (var f in fx.Values)
        {
            if (!IsWindow(f.Hwnd)) continue;
            GetWindowRect(f.Hwnd, out var r);
            var m = snap.Windows.FirstOrDefault(w => w.Hwnd == (long)f.Hwnd);
            wins.Add(new WinFacts((long)f.Hwnd, f.Name, f.Mode, r, Frames.FrameBounds(f.Hwnd), IsWindowVisible(f.Hwnd), IsIconic(f.Hwnd), IsZoomed(f.Hwnd),
                m is not null, m?.Lie ?? false, m?.Slot, m?.Revealed ?? false, m?.AwaitingZone ?? false, m?.NotCapable ?? false, FixtureState(f).Fs));
        }
        return new WorldFacts(mon.Bounds, mon.Work, snap.Foreground, Actor is { } a ? (long)a.Hwnd : 0, wins, snap.Leaks);
    }

    Trajectory Traj(Fx f, Ctx? ctx)
    {
        List<Sample> mine;
        lock (sg) mine = samples.Where(s => s.Hwnd == (long)f.Hwnd).ToList();
        var mon = Frames.AllMonitors().FirstOrDefault(m => m.Primary)?.Bounds ?? default;
        return Trajectories.Analyze((long)f.Hwnd, mine, ctx?.T0 ?? Now, ctx?.T1 ?? Now, mon, tpm);
    }

    // ------------------------------------------------------------------ judging an action

    static readonly (string Pattern, string Rule)[] BadLog =
    [
        (@"MISMATCH", "log-mismatch"), (@"\berror\b|exception|CRASH", "log-error"), (@"REFUSED", "log-refused"),
        (@"did not leave fullscreen in time", "log-no-leave"), (@"did not fill", "log-no-fill"), (@"lie missed", "log-lie-missed"),
        (@"lie not-capable", "log-not-capable"), (@"WIN32 FAIL", "log-win32"), (@"left out, next activation retries", "log-left-out"),
    ];

    void Analyze(Ctx ctx, Program.MgrSnap snap, bool timedOut)
    {
        var after = Facts(snap);
        var mon = after.Monitor;
        var ev = new Lazy<string>(() => BuildEvidence(ctx, after));
        void V(Violation v) => Add(v.Level == Level.Hard ? "FAIL" : "UX", v.Id, v.Message, curTest, curStep, ev.Value);

        if (timedOut) Add("FAIL", "settle-timeout", $"'{ctx.Label}': manager still busy after {opt["timeoutMs"]:F0}ms: {string.Join("; ", snap.NotQuiet)}", curTest, curStep, ev.Value);
        // The actor should hold the focus if it did before, or if the action was to give it the focus; an outside program moving a window does not change who is focused.
        var actorFocus = (ctx.Before.Foreground == ctx.ActorHwnd || ctx.ExpectFocus) && Desk.OnCurrentDesktop((IntPtr)ctx.ActorHwnd) ? ctx.ActorHwnd : 0;   // a window left on another desktop is not expected to hold the focus
        foreach (var v in Invariants.Check(after with { Actor = actorFocus })) V(v);

        // documented behaviour of the tiling keys
        if (ctx.Chord is { } ch && ch is "Win+Left" or "Win+Right" or "Win+Up" or "Win+Down"
            && ctx.Before.Windows.FirstOrDefault(f => f.Hwnd == ctx.ActorHwnd) is { } b && after.Windows.FirstOrDefault(f => f.Hwnd == ctx.ActorHwnd) is { } a)
            foreach (var v in TilingSpec.AfterKey(ch, b, a, mon, after.Work)) V(v);

        long[] fgSeq; lock (sg) fgSeq = [.. fgChanges.Where(x => x.Ticks >= ctx.T0 && x.Ticks <= ctx.T1).Select(x => x.Hwnd)];
        var emAll = Emits(ctx);
        // every window's trajectory
        var rec = new ActionRec { Group = curGroup, Test = curTest, Label = ctx.Label, Timeout = timedOut };
        foreach (var f in fx.Values)
        {
            if (!IsWindow(f.Hwnd)) continue;
            var tr = Traj(f, ctx);
            var expected = ctx.Expected.Contains((long)f.Hwnd);
            if (!expected)
            {
                if (fgSeq.Contains((long)f.Hwnd)) continue;   // it legitimately took the focus (the one before it went away), so it may have changed
                if (tr.Changed) Add("FAIL", "bystander-changed", $"{f.Name} was not acted on but changed: {Describe(tr)}", curTest, curStep, ev.Value);
                continue;
            }
            if (tr.Changed) { rec.FirstChangeMs = tr.FirstChangeMs; rec.SettleMs = tr.LastChangeMs; }
            rec.States = Math.Max(rec.States, tr.Distinct);
            foreach (var t in tr.Transients.Where(t => t.DurMs >= opt["glitchMs"]))
            {
                // Up to two frames of the app's own chrome inside ITS OWN F11 (the app restores its frame, then resizes): the manager can only type the key.
                if (t.DurMs <= opt["appFrameMs"] && t.Flags.HasFlag(WinFlag.Caption) && emAll.Any(e => e.Kind == "f11"))
                {
                    rec.AppFrames++;
                    Add("INFO", "app-frame", $"{f.Name}: the app drew its title bar for {t.DurMs:F0}ms inside its own fullscreen change", curTest, curStep);
                    continue;
                }
                rec.Transients++;
                Add("UX", "transient-state", $"{f.Name}: sat at {t.Rect}{(t.Flags.HasFlag(WinFlag.Caption) ? " with its title bar" : "")} for {t.DurMs:F0}ms between {tr.States[0].Rect} and {tr.States[^1].Rect}", curTest, curStep, ev.Value);
            }
            if (IdealFlips(ctx, f) is { } ideal && tr.StyleFlips > ideal)
                Add("UX", "style-flicker", $"{f.Name}: dropped and regained its borderless look {tr.StyleFlips} time(s); in-place would be {ideal}", curTest, curStep, ev.Value);
            if (tr.MaxOvershootPx > opt["overshootPx"])
                Add("UX", "overshoot", $"{f.Name}: went {tr.MaxOvershootPx}px outside the monitor during the action", curTest, curStep, ev.Value);
            if (tr.Changed && tr.FirstChangeMs > opt["firstChangeMs"] && ctx.HasKeys)
                Add("UX", "slow-first-response", $"{f.Name}: first visible change {tr.FirstChangeMs:F0}ms after the key (bar {opt["firstChangeMs"]:F0}ms)", curTest, curStep, ev.Value);
            if (tr.Changed && tr.LastChangeMs > opt["settleMs"])
                Add("UX", "slow-settle", $"{f.Name}: still moving {tr.LastChangeMs:F0}ms after the key (bar {opt["settleMs"]:F0}ms)", curTest, curStep, ev.Value);
        }

        // When the window being operated on closed or minimized, the focus MUST move on: that is not a bounce.
        var actorGone = ctx.ActorHwnd != 0 && (!IsWindow((IntPtr)ctx.ActorHwnd) || IsIconic((IntPtr)ctx.ActorHwnd));
        var strangers = actorGone ? [] : fgSeq.Where(h => h != 0 && !ctx.Expected.Contains(h) && h != ctx.ActorHwnd).Distinct().ToList();
        if (strangers.Count > 0)
            Add("UX", "foreground-bounce", $"focus visited {string.Join(", ", strangers.Select(h => fx.Values.FirstOrDefault(x => (long)x.Hwnd == h)?.Name ?? $"0x{h:X}"))} during '{ctx.Label}'", curTest, curStep, ev.Value);

        // responsiveness
        double[] lat; lock (sg) lat = [.. loopLat.Where(x => x.Ticks >= ctx.T0 && x.Ticks <= ctx.T1).Select(x => x.Ms)];
        var ls = Stats.Summarize(lat);
        rec.LoopMaxMs = ls.Max; rec.LoopP95Ms = ls.P95;
        if (ls.Max >= opt["loopSlowMs"])
            Add("UX", "loop-stall", $"the manager's loop was unavailable for {ls.Max:F0}ms during '{ctx.Label}' ({lat.Count(x => x >= opt["loopSlowMs"])} of {lat.Length} probes over {opt["loopSlowMs"]:F0}ms)", curTest, curStep, ev.Value);
        var kq = Program.KeyDispatches.Skip(ctx.KeyDispatchIdx).Where(k => k.Post >= ctx.T0 && k.Post <= ctx.T1).Select(k => Ms(k.Dispatch - k.Post)).ToArray();
        rec.KeyQueueMs = kq.Length == 0 ? 0 : kq.Max();
        if (rec.KeyQueueMs >= opt["keyQueueMs"])
            Add("UX", "key-queued", $"a key waited {rec.KeyQueueMs:F0}ms for the loop before it was even looked at", curTest, curStep, ev.Value);

        // what the application would have seen on its keyboard
        var em = Emits(ctx);
        rec.F11 = em.Count(e => e.Kind == "f11");
        if (ctx.HasKeys)
        {
            var stream = AppStream(em);
            foreach (var v in AppView.Check(stream, ctx.PhysicalAtStart, physical)) V(v);
        }

        // the manager's own log for the window of this action
        foreach (var line in LogLines(ctx))
            foreach (var (pat, rule) in BadLog)
                if (Regex.IsMatch(line, pat, RegexOptions.IgnoreCase) && !allowLog.Any(r => r.IsMatch(line)) && !line.Contains("SLOW"))
                    Add("FAIL", rule, line.Length > 220 ? line[..220] : line, curTest, curStep, ev.Value);

        lock (findings) actionRecs.Add(rec);
        ctx.Evidence = "";
        if (actionRecs.Count % 25 == 0) { try { WriteReports(); } catch { } } // a killed run still leaves readings
        if (actionRecs.Count % 10 == 0) Note($"{actionRecs.Count} actions judged, {findings.Count(f => f.Sev == "FAIL")} hard / {findings.Count(f => f.Sev == "UX")} quality findings so far");
    }

    // How many times should the window's borderless look flip during this action at best? null = no opinion.
    static int? IdealFlips(Ctx ctx, Fx f)
    {
        var wasLie = ctx.Before.Windows.FirstOrDefault(w => w.Hwnd == (long)f.Hwnd)?.Lie ?? false;
        return ctx.Chord switch
        {
            "Win+Left" or "Win+Right" or "Win+Up" when wasLie => 0,   // re-slotting a fullscreen window should not leave fullscreen
            "Alt+T" or "Win+Alt" => 1,
            _ => null,
        };
    }

    List<AppKey> AppStream(Desk.Emit[] em) =>
        [.. em.Where(e => e.Kind is "key" or "pass").OrderBy(e => e.Ticks).Select(e => new AppKey(e.Ticks, e.Vk, e.Up, e.Kind == "pass" ? Src.Pass : Src.Injected))];

    string BuildEvidence(Ctx ctx, WorldFacts after)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"action: {ctx.Label}");
        sb.AppendLine("before:"); sb.AppendLine(Indent(Describe(ctx.Before)));
        sb.AppendLine("after:"); sb.AppendLine(Indent(Describe(after)));
        foreach (var f in fx.Values.Where(f => ctx.Expected.Contains((long)f.Hwnd) && IsWindow(f.Hwnd)))
            sb.AppendLine($"trajectory {f.Name}: {Describe(Traj(f, ctx))}");
        var em = Emits(ctx);
        if (em.Length > 0)
            sb.AppendLine("emitted: " + string.Join(" ", em.Take(60).Select(e => e.Kind switch
            {
                "key" => $"{(e.Up ? "-" : "+")}{Chord.NameOf(e.Vk)}", "pass" => $"({(e.Up ? "-" : "+")}{Chord.NameOf(e.Vk)})",
                "foreground" => $"fg:0x{e.Target:X}", "f11" => "F11", "wheel" => $"wheel{e.Data}", "hwheel" => $"hwheel{e.Data}", _ => e.Kind,
            })));
        var log = LogLines(ctx).Where(l => !l.Contains("testkey")).Take(14).ToList();
        if (log.Count > 0) { sb.AppendLine("log:"); foreach (var l in log) sb.AppendLine("  " + (l.Length > 200 ? l[..200] : l)); }
        return sb.ToString().TrimEnd();
    }

    static string Indent(string s) => string.Join("\n", s.Split('\n').Select(l => "  " + l));

    // ------------------------------------------------------------------ the action alphabet

    static readonly string[] Chords = ["Win+Left", "Win+Right", "Win+Up", "Win+Down", "Alt+M", "Alt+N", "Alt+T", "Win+Alt"];

    // ShowWindow from outside. Windows activates a minimized window that is restored or maximized, so the focus follows it.
    void ShowExternally(Fx f, int cmd)
    {
        var wasIconic = IsIconic(f.Hwnd);
        ShowWindow(f.Hwnd, cmd);
        if (wasIconic) Desk.SetForeground(f.Hwnd);
    }

    void UserF11(Fx f)
    {
        PostMessage(f.Hwnd, 0x100, 0x7A, 0x00570001);
        Thread.Sleep(15);
        PostMessage(f.Hwnd, 0x101, 0x7A, unchecked((nint)0xC0570001));
    }

    // One user-level step, judged: perform, wait for the manager to settle, check everything.
    void Act(string name)
    {
        // Keys go to whichever window really has the focus; judge THAT one, not whichever the macro last named.
        var fgNow = Desk.Foreground();
        if (fx.Values.FirstOrDefault(x => x.Hwnd == fgNow) is { } focused && name is not ("activate" or "other" or "back" or "hover-b")) actorName = focused.Name;
        var f = Actor ?? throw new InvalidOperationException("no actor window");
        Fx? b = fx.GetValueOrDefault("B");
        var area = Frames.AllMonitors().First(m => m.Primary);
        if (Chords.Contains(name)) { Begin(name, chord: name); KeyChord(name); }
        else switch (name)
        {
            case "activate": Begin(name, expected: [f.Hwnd]); ActivateWindow(f); break;
            case "appf11": if (IsIconic(f.Hwnd)) { history.Add(name + " (skipped: minimized)"); return; } Begin(name, expected: [f.Hwnd]); UserF11(f); break;   // nobody can press F11 in a minimized app
            case "other": { var bb = b ?? throw new InvalidOperationException("no window B"); actorName = "B"; Begin(name, expected: [bb.Hwnd]); ActivateWindow(bb); break; }
            case "back": { var aa = fx["A"]; actorName = "A"; Begin(name, expected: [aa.Hwnd]); ActivateWindow(aa); break; }
            case "hover-b": { var bb = b ?? throw new InvalidOperationException("no window B"); GetWindowRect(bb.Hwnd, out var r); var (cx, cy) = r.Center;
                // Hovering the middle of B focuses whatever is actually THERE: B if it is visible, else the window covering it.
                var under = GetAncestor(WindowFromPoint(new POINT { X = cx, Y = cy }), GA_ROOT);
                var target = fx.Values.FirstOrDefault(x => x.Hwnd == under) ?? bb; actorName = target.Name; Begin(name, expected: [target.Hwnd]); Desk.SetCursor(cx, cy); Thread.Sleep(900); break; }
            case "ext-left": { Begin(name, expected: [f.Hwnd]); var r = TilingSpec.LeftHalf(area.Work); SetWindowPos(f.Hwnd, IntPtr.Zero, r.Left, r.Top, r.Width, r.Height, SWP_NOZORDER | SWP_NOACTIVATE); break; }
            case "ext-big": { Begin(name, expected: [f.Hwnd]); SetWindowPos(f.Hwnd, IntPtr.Zero, area.Work.Left + 80, area.Work.Top + 60, area.Work.Width * 3 / 5, area.Work.Height * 3 / 5, SWP_NOZORDER | SWP_NOACTIVATE); break; }
            case "ext-max": Begin(name, expected: [f.Hwnd]); ShowExternally(f, SW_MAXIMIZE); break;
            case "ext-min": Begin(name, expected: [f.Hwnd]); ShowWindow(f.Hwnd, SW_MINIMIZE); break;
            case "ext-restore": Begin(name, expected: [f.Hwnd]); ShowExternally(f, SW_RESTORE); break;
            default: throw new FormatException($"unknown action '{name}'");
        }
        Settle();
        history.Add(name);
    }
    readonly List<string> history = [];

    // Get window A of the given mode into the canonical start: alive, restored, in its own fullscreen filling the monitor, foreground.
    // Observed but never judged. A window that cannot get there is replaced, once.
    void Normalize(string mode, bool bystander)
    {
        var was = quiet; quiet = true;
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (!fx.TryGetValue("A", out var a) || a.Mode != mode || a.Proc.HasExited || !IsWindow(a.Hwnd) || attempt > 0) { Spawn("A", mode); a = fx["A"]; }
                if (bystander && (!fx.TryGetValue("B", out var bf) || bf.Proc.HasExited || !IsWindow(bf.Hwnd))) Spawn("B", "std", new Rect(1180, 120, 1180 + 560, 120 + 420), activate: false);
                actorName = "A";
                ActivateWindow(a);
                Begin("normalize"); cur!.Expected.Add((long)a.Hwnd); Settle(analyze: false);
                if (Canonical(mode)) break;
                Begin("normalize Win+Up"); KeyChord("Win+Up"); Settle(analyze: false);
                if (Canonical(mode)) break;
            }
        }
        finally { quiet = was; }
        history.Clear();
        if (!Canonical(mode)) Add("FAIL", "normalize-failed", $"could not bring a '{mode}' window to its starting state", curTest, curStep, Describe(Facts()));
    }

    bool Canonical(string mode)
    {
        var w = Facts();
        var f = w.Windows.FirstOrDefault(x => x.Name == "A");
        if (f is null || f.Iconic || w.Foreground != f.Hwnd) return false;
        if (mode is "nof11") return !f.Lie;
        return f.Lie && f.Slot is { } s && Invariants.Near(s, w.Monitor, 4) && Invariants.Near(f.Rect, w.Monitor, 8);
    }

    void Singles(string mode, string[] actions, bool bystander)
    {
        foreach (var act in actions)
        {
            EndTest(); curGroup = curGroup.Length > 0 ? curGroup : "single actions";
            StartTest($"single[{mode}] {act}");
            Normalize(mode, bystander);
            if (!Reachable(act)) continue;
            Act(act);
            EndTest();
        }
    }

    void Pairs(string mode, string[] actions, bool bystander)
    {
        foreach (var x in actions) foreach (var y in actions)
        {
            EndTest();
            StartTest($"pair[{mode}] {x} then {y}");
            Normalize(mode, bystander);
            if (!Reachable(x) || !Reachable(y)) continue;
            Act(x); Act(y);
            EndTest();
        }
    }

    bool Reachable(string act)
    {
        if (act is "other" or "back" or "hover-b" && !fx.ContainsKey("B")) { skipRest = "needs a bystander window"; return false; }
        return true;
    }

    // A reproducible random sequence of user actions; the invariants are judged after every one. A failure shows the whole sequence so far.
    void Walk(string mode, int seed, int steps, string[] actions, bool bystander)
    {
        EndTest();
        StartTest($"walk[{mode}] seed={seed} steps={steps}");
        var rng = new Random(seed);
        Normalize(mode, bystander);
        var failsBefore = curRec!.Fails;
        for (var i = 0; i < steps; i++)
        {
            var act = actions[rng.Next(actions.Length)];
            if (!Reachable(act)) continue;
            var before = curRec.Fails;
            try { Act(act); }
            catch (Exception e) { Add("FAIL", "macro-error", $"step {i + 1} '{act}': {e.Message}", curTest, curStep, e.ToString()); Normalize(mode, bystander); continue; }
            if (curRec.Fails > before)
            {
                Add("INFO", "walk-sequence", $"after step {i + 1}: {string.Join(" > ", history.TakeLast(12))}", curTest, curStep);
                Normalize(mode, bystander);   // a broken state would only repeat itself: restart from the canonical one
            }
        }
        EndTest();
    }

    // ------------------------------------------------------------------ hints

    void HintShow()
    {
        Begin("Alt+F", chord: "Alt+F"); var t = Now;
        KeyChord("Alt+F");
        var deadline = Now + (long)(opt["hintsMs"] * 4 * tpm); var shown = false;
        while (Now < deadline) { if (Loop(Program.TakeSnapshot).HintsActive) { shown = true; break; } Thread.Sleep(8); }
        var ms = Ms(Now - t);
        notes["hints-ms"] = ms.ToString("F0");
        Check(shown, "hints-not-shown", $"Alt+F produced no badges within {opt["hintsMs"] * 4:F0}ms");
        if (shown && ms > opt["hintsMs"]) Add("UX", "slow-hints", $"badges appeared {ms:F0}ms after Alt+F (bar {opt["hintsMs"]:F0}ms)", curTest, curStep);
    }

    // ------------------------------------------------------------------ expectations

    void ExpectWindow(string[] a)
    {
        var f = fx[a[0]];
        var w = Facts();
        var fact = w.Windows.First(x => x.Hwnd == (long)f.Hwnd);
        if (a[1] == "at")
        {
            var spec = a[2];
            Rect target = spec switch
            {
                "left" => TilingSpec.LeftHalf(w.Monitor), "right" => TilingSpec.RightHalf(w.Monitor), "full" => w.Monitor,
                "workleft" => TilingSpec.LeftHalf(w.Work), "workright" => TilingSpec.RightHalf(w.Work), "work" => w.Work,
                _ when spec.Contains(',') => ParseRect(spec),
                _ => throw new FormatException($"unknown place '{spec}'"),
            };
            var seen = fact.Lie || fact.Frame is null ? fact.Rect : fact.Frame.Value;
            var tol = fact.Lie ? 12 : fact.Frame is null ? 16 : 2;   // 12: a snapped window's invisible borders (about 9px) hang outside the zone
            Check(Invariants.Near(seen, target, tol), "expect-at", $"{a[0]} is at {seen}, expected {spec} = {target}", Describe(w));
            return;
        }
        var flag = a[1]; var want = !flag.StartsWith('!'); flag = flag.TrimStart('!');
        bool? got = flag switch
        {
            "lie" => fact.Lie, "revealed" => fact.Revealed, "iconic" => fact.Iconic, "zoomed" => fact.Zoomed, "managed" => fact.Managed,
            "fg" => w.Foreground == fact.Hwnd, "appfs" => fact.AppFullscreen, "notcapable" => fact.NotCapable, "visible" => fact.Visible,
            _ => throw new FormatException($"unknown flag '{flag}'"),
        };
        Check(got == want, $"expect-{flag}", $"{a[0]}: {flag} is {got}, expected {want}", Describe(w));
    }

    static Rect ParseRect(string s)
    {
        var p = s.Split(',').Select(int.Parse).ToArray();
        return new Rect(p[0], p[1], p[0] + p[2], p[1] + p[3]);
    }

    void ExpectTap(int vk, int count)
    {
        var n = Emissions.CountTaps(AppStream(Emits(last)), vk);
        Check(count < 0 ? n >= 1 : n == count, "expect-tap", $"{Chord.NameOf(vk)} was typed {n} time(s), expected {(count < 0 ? "at least 1" : count.ToString())}", EmitText());
    }

    void ExpectWheel(string dir, int count)
    {
        var kind = dir is "left" or "right" ? "hwheel" : "wheel";
        var sign = dir is "down" or "left" ? -1 : 1;
        var n = Emits(last).Count(e => e.Kind == kind && Math.Sign(e.Data) == sign);
        Check(n == count, "expect-wheel", $"{n} wheel step(s) {dir}, expected {count}", EmitText());
    }

    void ExpectClick()
    {
        var em = Emits(last);
        Check(em.Any(e => e.Kind == "click-down") && em.Any(e => e.Kind == "click-up"), "expect-click", "no left click was emitted", EmitText());
    }

    void ExpectNoOutput()
    {
        var out_ = Emits(last).Where(e => (e.Kind == "key" && e.Vk != Vk.Mask && !Vk.IsModifier(e.Vk)) || e.Kind is "wheel" or "hwheel" or "click-down" or "f11").ToList();
        Check(out_.Count == 0, "expect-no-output", $"{out_.Count} unexpected output event(s): {string.Join(" ", out_.Select(e => e.Kind == "key" ? Chord.NameOf(e.Vk) : e.Kind))}", EmitText());
    }

    // Pure key-stream rules (no stuck keys, nothing typed under Alt/Win, Alt/Win release masked) for the last action.
    void ExpectCleanKeys()
    {
        var v = AppView.Check(AppStream(Emits(last)), last?.PhysicalAtStart, physical);
        Check(v.Count == 0, "expect-clean", string.Join("; ", v.Select(x => x.Message)), EmitText());
    }

    string EmitText() => string.Join(" ", Emits(last).Take(60).Select(e => e.Kind switch
    {
        "key" => $"{(e.Up ? "-" : "+")}{Chord.NameOf(e.Vk)}", "pass" => $"({(e.Up ? "-" : "+")}{Chord.NameOf(e.Vk)})", "wheel" => $"wheel{e.Data}", "hwheel" => $"hwheel{e.Data}", _ => e.Kind,
    }));

    void ExpectCount(string what, int n, string spec)
    {
        var ok = spec.StartsWith("<=") ? n <= int.Parse(spec[2..]) : spec.StartsWith(">=") ? n >= int.Parse(spec[2..]) : n == int.Parse(spec);
        Check(ok, $"expect-{what}", $"{n} {what}, expected {spec}", EmitText());
    }

    void ExpectHints(string[] a)
    {
        var s = Loop(Program.TakeSnapshot);
        switch (a[0])
        {
            case "active": Check(s.HintsActive, "hints-inactive", "hints are not shown"); break;
            case "inactive": Check(!s.HintsActive, "hints-active", "hints are still shown"); break;
            case "min": Check(s.HintTargets >= int.Parse(a[1]), "hints-targets", $"{s.HintTargets} target(s), expected at least {a[1]}"); break;
            case "max": Check(s.HintTargets <= int.Parse(a[1]), "hints-targets", $"{s.HintTargets} target(s), expected at most {a[1]}"); break;
            case "typed": { var want = a.Length > 1 ? a[1] : ""; Check(s.HintsTyped == want, "hints-typed", $"typed '{s.HintsTyped}', expected '{want}'"); break; }
        }
    }

    void ExpectMetric(string name, string op, double value)
    {
        var r = actionRecs.LastOrDefault();
        double? got = name switch
        {
            "first-change-ms" => r?.FirstChangeMs, "settle-ms" => r?.SettleMs, "key-queue-ms" => r?.KeyQueueMs, "loop-max-ms" => r?.LoopMaxMs,
            "loop-p95-ms" => r?.LoopP95Ms, "f11" => r?.F11, "hints-ms" => double.TryParse(notes.GetValueOrDefault("hints-ms"), out var h) ? h : null,
            _ => throw new FormatException($"unknown metric '{name}'"),
        };
        if (got is not { } g) { Check(false, "expect-metric", $"no reading for {name}"); return; }
        var ok = op switch { "<" => g < value, "<=" => g <= value, ">" => g > value, ">=" => g >= value, "==" => Math.Abs(g - value) < 0.5, _ => throw new FormatException($"unknown operator '{op}'") };
        Check(ok, "expect-metric", $"{name} = {g:F0}, expected {op} {value}");
    }

    // Time from the first key of the action to the first emission of that kind (wheel, f11, click-down...).
    void ExpectEmitLatency(string kind, string op, double ms)
    {
        var first = Program.KeyDispatches.Skip(last?.KeyDispatchIdx ?? 0).Select(k => (long?)k.Post).FirstOrDefault();
        var em = Emits(last).FirstOrDefault(e => e.Kind == kind);
        if (first is null || em.Kind is null) { Check(false, "expect-emit-latency", $"no {kind} emission to time"); return; }
        var got = Ms(em.Ticks - first.Value);
        var ok = op == "<" ? got < ms : got <= ms;
        notes[$"{kind}-latency"] = got.ToString("F0");
        Check(ok, "expect-emit-latency", $"first {kind} came {got:F0}ms after the key, expected {op} {ms:F0}ms", EmitText());
    }

    // The last test: the quit chord. The manager restores every window and exits; Finish() then judges what it left behind.
    void QuitAndFinish()
    {
        Begin("Ctrl+Alt+Shift+Q", chord: "Ctrl+Alt+Shift+Q");
        KeyChord("Ctrl+Alt+Shift+Q");
        Thread.Sleep(25000);   // the process normally ends here, from the loop thread, and reports are written by Finish()
        Add("FAIL", "quit-did-not-exit", "the manager was still running 25s after Ctrl+Alt+Shift+Q", curTest, curStep);
    }

    // ------------------------------------------------------------------ after the manager shut down

    void AfterShutdown()
    {
        curTest = "shutdown"; curStep = "after the manager quit"; curGroup = "Shutdown";
        curRec = new TestRec { Group = curGroup, Name = "quit restores every window" };
        tests.Add(curRec);
        foreach (var f in fx.Values)
        {
            if (f.Proc.HasExited || !IsWindow(f.Hwnd)) continue;
            var st = FixtureState(f);
            Check(st.Fs != true, "shutdown-left-fullscreen", $"{f.Name} ({f.Mode}) is still in its own fullscreen after the manager quit");
            Check(!IsIconic(f.Hwnd), "shutdown-left-minimized", $"{f.Name} is minimized after the manager quit");
        }
    }
}
