using System.Globalization;
using System.Text.RegularExpressions;

namespace WindowManager;

// Pure logic of the sandbox test runner: no Windows calls, so every rule here has a unit test. The runner
// (SandboxRunner.cs) gathers facts from the live manager and feeds them in; this decides what is wrong.

public enum Level { Hard, Ux }   // Hard = a defect against the documented behaviour; Ux = a quality bar the user asked for

public sealed record Violation(string Id, string Message, long Hwnd = 0, Level Level = Level.Hard);

// ---- chords ----

public static class Chord
{
    static readonly Dictionary<string, int> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Win"] = Vk.LWin, ["LWin"] = Vk.LWin, ["RWin"] = Vk.RWin,
        ["Alt"] = Vk.LAlt, ["LAlt"] = Vk.LAlt, ["RAlt"] = Vk.RAlt,
        ["Ctrl"] = Vk.LCtrl, ["LCtrl"] = Vk.LCtrl, ["RCtrl"] = Vk.RCtrl,
        ["Shift"] = Vk.LShift, ["LShift"] = Vk.LShift, ["RShift"] = Vk.RShift,
        ["Left"] = Vk.Left, ["Right"] = Vk.Right, ["Up"] = Vk.Up, ["Down"] = Vk.Down,
        ["Esc"] = Vk.Esc, ["Enter"] = Vk.Enter, ["PgUp"] = Vk.PgUp, ["PgDn"] = Vk.PgDn, ["Home"] = Vk.Home, ["End"] = Vk.End,
        ["Tab"] = 0x09, ["Space"] = 0x20, ["Apps"] = Vk.Apps, ["Mask"] = Vk.Mask,
        ["["] = Vk.OemOpen, ["]"] = Vk.OemClose, ["F23"] = Vk.F23,
    };
    static readonly Dictionary<int, string> Reverse = BuildReverse();
    static Dictionary<int, string> BuildReverse()
    {
        var d = new Dictionary<int, string>();
        foreach (var (k, v) in Names) d.TryAdd(v, k);
        d[Vk.LWin] = "Win"; d[Vk.LAlt] = "Alt"; d[Vk.LCtrl] = "Ctrl"; d[Vk.LShift] = "Shift";
        return d;
    }

    // "Win+Alt+Left" -> virtual keys in press order. Release is the reverse of this.
    public static int[] Parse(string chord) => [.. chord.Split('+', StringSplitOptions.RemoveEmptyEntries).Select(One)];

    public static int One(string name)
    {
        if (Names.TryGetValue(name, out var vk)) return vk;
        if (name.Length == 1 && char.IsLetterOrDigit(name[0])) return char.ToUpperInvariant(name[0]);
        if (name.Length is 2 or 3 && (name[0] is 'F' or 'f') && int.TryParse(name.AsSpan(1), out var n) && n is >= 1 and <= 24) return 0x70 + n - 1;
        throw new FormatException($"unknown key '{name}'");
    }

    public static string NameOf(int vk) =>
        Reverse.TryGetValue(vk, out var n) ? n
        : vk is >= 0x70 and <= 0x87 ? $"F{vk - 0x70 + 1}"
        : vk is >= '0' and <= '9' or >= 'A' and <= 'Z' ? ((char)vk).ToString()
        : $"0x{vk:X2}";
}

// ---- macro text ----

public sealed record Cmd(int Line, string Raw, string Verb, string[] Args);

public static class MacroParser
{
    // One command per line: verb then space separated arguments. '#' starts a comment. Blank lines are ignored.
    public static List<Cmd> Parse(string text)
    {
        var list = new List<Cmd>();
        var n = 0;
        foreach (var raw in text.Split('\n'))
        {
            n++;
            var line = raw.Trim();
            var hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash].Trim();
            if (line.Length == 0) continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            list.Add(new Cmd(n, line, parts[0].ToLowerInvariant(), parts[1..]));
        }
        return list;
    }

    // `for x in a,b,c` ... `endfor` repeats its body once per value, replacing $x. Nests. Keeps tables of cases short.
    public static List<Cmd> Expand(List<Cmd> cmds)
    {
        var result = new List<Cmd>();
        ExpandInto(cmds, 0, cmds.Count, new Dictionary<string, string>(), result);
        return result;
    }

    static void ExpandInto(List<Cmd> cmds, int start, int end, Dictionary<string, string> vars, List<Cmd> result)
    {
        for (var i = start; i < end; i++)
        {
            var c = cmds[i];
            if (c.Verb != "for") { result.Add(Substitute(c, vars)); continue; }
            if (c.Args.Length < 3 || c.Args[1] != "in") throw new FormatException($"line {c.Line}: expected `for NAME in a,b,c`");
            var depth = 1; var close = i + 1;
            for (; close < end; close++)
            {
                if (cmds[close].Verb == "for") depth++;
                else if (cmds[close].Verb == "endfor" && --depth == 0) break;
            }
            if (close >= end) throw new FormatException($"line {c.Line}: `for` without `endfor`");
            foreach (var value in string.Join(" ", c.Args.Skip(2).Select(a => Substitute(a, vars))).Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var inner = new Dictionary<string, string>(vars) { [c.Args[0]] = value };
                ExpandInto(cmds, i + 1, close, inner, result);
            }
            i = close;
        }
    }

    static string Substitute(string s, Dictionary<string, string> vars)
    {
        foreach (var (k, v) in vars.OrderByDescending(kv => kv.Key.Length)) s = s.Replace("$" + k, v);
        return s;
    }

    static Cmd Substitute(Cmd c, Dictionary<string, string> vars) =>
        vars.Count == 0 ? c : new Cmd(c.Line, Substitute(c.Raw, vars), c.Verb, [.. c.Args.Select(a => Substitute(a, vars))]);
}

// ---- statistics ----

public sealed record Summary(int Count, double Min, double P50, double P95, double Max);

public static class Stats
{
    public static double Percentile(IReadOnlyList<double> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        var rank = p / 100.0 * (sorted.Count - 1);
        var lo = (int)Math.Floor(rank); var hi = (int)Math.Ceiling(rank);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
    }

    public static Summary Summarize(IEnumerable<double> values)
    {
        var s = values.OrderBy(v => v).ToList();
        return s.Count == 0 ? new Summary(0, 0, 0, 0, 0) : new Summary(s.Count, s[0], Percentile(s, 50), Percentile(s, 95), s[^1]);
    }
}

// ---- trajectories: what a window did, sampled every few milliseconds ----

[Flags] public enum WinFlag { None = 0, Visible = 1, Iconic = 2, Zoomed = 4, Caption = 8, Frame = 16 }

public readonly record struct Sample(long Ticks, long Hwnd, Rect Rect, WinFlag Flags);

public sealed record Dwell(Rect Rect, WinFlag Flags, double StartMs, double DurMs);

public sealed record Trajectory(long Hwnd, IReadOnlyList<Dwell> States, double FirstChangeMs, double LastChangeMs,
    double MaxTransientMs, int MaxOvershootPx, int StyleFlips, int VisibilityFlips)
{
    public int Distinct => States.Count;
    public bool Changed => States.Count > 1;
    // States between the first and the last: the window was SEEN somewhere that is neither where it started nor where it ended.
    public IEnumerable<Dwell> Transients => States.Count > 2 ? States.Skip(1).Take(States.Count - 2) : [];
}

public static class Trajectories
{
    // samples: this window's samples in time order (changes only). t0..t1 is the action window, in ticks.
    public static Trajectory Analyze(long hwnd, IReadOnlyList<Sample> samples, long t0, long t1, Rect monitor, double ticksPerMs)
    {
        var states = new List<Dwell>();
        Sample? last = null;
        foreach (var s in samples)
        {
            if (s.Ticks > t1) break;
            if (s.Ticks < t0) { last = s; continue; }
            if (states.Count == 0 && last is { } before && (before.Rect != s.Rect || before.Flags != s.Flags))
                states.Add(new Dwell(before.Rect, before.Flags, 0, 0));   // where it was when the action began
            else if (states.Count == 0 && last is null) states.Add(new Dwell(s.Rect, s.Flags, 0, 0));
            if (states.Count > 0 && states[^1].Rect == s.Rect && states[^1].Flags == s.Flags) continue;
            states.Add(new Dwell(s.Rect, s.Flags, (s.Ticks - t0) / ticksPerMs, 0));
        }
        if (states.Count == 0 && last is { } only) states.Add(new Dwell(only.Rect, only.Flags, 0, 0));
        var total = (t1 - t0) / ticksPerMs;
        for (var i = 0; i < states.Count; i++)
        {
            var end = i + 1 < states.Count ? states[i + 1].StartMs : total;
            states[i] = states[i] with { DurMs = Math.Max(0, end - states[i].StartMs) };
        }
        double first = 0, lastChange = 0, maxTransient = 0;
        int overshoot = 0, styleFlips = 0, visFlips = 0;
        int Excess(Dwell d) => (d.Flags & WinFlag.Visible) != 0 && (d.Flags & WinFlag.Iconic) == 0
            ? Math.Max(Math.Max(monitor.Left - d.Rect.Left, monitor.Top - d.Rect.Top), Math.Max(d.Rect.Right - monitor.Right, d.Rect.Bottom - monitor.Bottom)) : 0;
        var startExcess = states.Count > 0 ? Excess(states[0]) : 0;   // where the window was put before the action is not the manager's doing
        if (states.Count > 1) { first = states[1].StartMs; lastChange = states[^1].StartMs; }
        for (var i = 1; i < states.Count - 1; i++) maxTransient = Math.Max(maxTransient, states[i].DurMs);
        for (var i = 0; i < states.Count; i++)
        {
            if (i > 0) overshoot = Math.Max(overshoot, Excess(states[i]) - startExcess);
            if (i == 0) continue;
            var d = states[i - 1].Flags ^ states[i].Flags;
            if ((d & (WinFlag.Caption | WinFlag.Frame)) != 0) styleFlips++;
            if ((d & (WinFlag.Visible | WinFlag.Iconic)) != 0) visFlips++;
        }
        return new Trajectory(hwnd, states, first, lastChange, maxTransient, Math.Max(0, overshoot), styleFlips, visFlips);
    }
}

// ---- what is true of the world after an action settled ----

public sealed record WinFacts(long Hwnd, string Name, string Mode, Rect Rect, Rect? Frame, bool Visible, bool Iconic, bool Zoomed,
    bool Managed, bool Lie, Rect? Slot, bool Revealed, bool AwaitingZone, bool NotCapable, bool? AppFullscreen);

public sealed record WorldFacts(Rect Monitor, Rect Work, long Foreground, long Actor, IReadOnlyList<WinFacts> Windows, IReadOnlyList<string> Leaks);

public static class Invariants
{
    public const int SlotTol = 8;

    public static bool Near(Rect a, Rect b, int tol) =>
        Math.Abs(a.Left - b.Left) <= tol && Math.Abs(a.Top - b.Top) <= tol && Math.Abs(a.Right - b.Right) <= tol && Math.Abs(a.Bottom - b.Bottom) <= tol;

    // Rules that must hold whenever the manager is idle, whatever sequence of actions led here.
    public static List<Violation> Check(WorldFacts w)
    {
        var v = new List<Violation>();
        foreach (var f in w.Windows)
        {
            if (!f.Managed) continue;
            if (f.Lie && f.Revealed) v.Add(new("lie-and-revealed", $"{f.Name}: marked both fullscreen-in-slot and revealed", f.Hwnd));
            // A window minimized from outside stays in its own fullscreen, so lie + iconic is fine; it is only wrong if the app is NOT fullscreen.
            if (f.Lie && f.Iconic && f.AppFullscreen == false) v.Add(new("iconic-lie-mismatch", $"{f.Name}: minimized and counted as fullscreen-in-slot, but the app is not fullscreen", f.Hwnd));
            if (f.Lie && !f.Iconic)
            {
                if (f.Slot is not { } slot) v.Add(new("lie-without-slot", $"{f.Name}: fullscreen-in-slot with no slot", f.Hwnd));
                else
                {
                    if (!Near(f.Rect, slot, SlotTol)) v.Add(new("lie-fills-slot", $"{f.Name}: rect {f.Rect} does not fill its slot {slot}", f.Hwnd));
                    if (Clamp(slot, w.Monitor) != slot || slot.Width <= 0 || slot.Height <= 0) v.Add(new("slot-on-screen", $"{f.Name}: slot {slot} is not inside the monitor {w.Monitor}", f.Hwnd));
                }
            }
            // What the manager believes must be what the app believes. A desync is how a window ends up toggled the wrong way.
            if (f.AppFullscreen is { } fs && !f.Iconic && !f.AwaitingZone && !f.Revealed && fs != f.Lie)   // a revealed window is the user's to drive: its own F11 is its own business
                v.Add(new("app-fullscreen-matches-lie", $"{f.Name}: manager says fullscreen-in-slot={f.Lie} but the app says fullscreen={fs}", f.Hwnd));
        }
        // Side by side: left and right halves must meet exactly (the "gap in the middle" bug) and never overlap.
        var lies = w.Windows.Where(f => f is { Lie: true, Iconic: false, Slot: not null }).ToList();
        foreach (var a in lies) foreach (var b in lies)
        {
            if (a.Hwnd >= b.Hwnd) continue;
            var (l, r) = a.Slot!.Value.Left <= b.Slot!.Value.Left ? (a, b) : (b, a);
            var isLeftHalf = l.Slot!.Value.Left == w.Monitor.Left && l.Slot.Value.Width < w.Monitor.Width;
            var isRightHalf = r.Slot!.Value.Right == w.Monitor.Right && r.Slot.Value.Width < w.Monitor.Width;
            if (!isLeftHalf || !isRightHalf) continue;
            if (Math.Abs(l.Rect.Right - r.Rect.Left) > 2)
                v.Add(new("tiles-adjoin", $"{l.Name} ends at {l.Rect.Right}, {r.Name} starts at {r.Rect.Left}: {(l.Rect.Right < r.Rect.Left ? "gap" : "overlap")} of {Math.Abs(l.Rect.Right - r.Rect.Left)}px", l.Hwnd));
        }
        if (w.Actor != 0 && w.Windows.FirstOrDefault(f => f.Hwnd == w.Actor) is { Iconic: false } && w.Foreground != w.Actor)
            v.Add(new("foreground-is-actor", $"foreground is 0x{w.Foreground:X} but the window being operated on is 0x{w.Actor:X} (a bystander took the focus)", w.Foreground));
        foreach (var leak in w.Leaks) v.Add(new("no-orphan-state", leak));
        return v;
    }

    public static Rect Clamp(Rect r, Rect b) => new(Math.Max(r.Left, b.Left), Math.Max(r.Top, b.Top), Math.Min(r.Right, b.Right), Math.Min(r.Bottom, b.Bottom));
}

// ---- documented behaviour of the tiling keys, as postconditions ----

public static class TilingSpec
{
    public static Rect LeftHalf(Rect a) => a with { Right = a.Left + a.Width / 2 };
    public static Rect RightHalf(Rect a) => a with { Left = a.Left + a.Width / 2 };

    // What an action must have achieved. before/after are the actor's facts either side of it.
    public static List<Violation> AfterKey(string chord, WinFacts before, WinFacts after, Rect monitor, Rect work)
    {
        var v = new List<Violation>();
        if (before.Iconic || !before.Managed) return v;
        Rect? want = chord switch
        {
            "Win+Left" => before.Lie ? LeftHalf(monitor) : LeftHalf(work),
            "Win+Right" => before.Lie ? RightHalf(monitor) : RightHalf(work),
            "Win+Up" when before.Lie => monitor,
            _ => null,
        };
        if (chord == "Win+Down")
        {
            if (!after.Iconic) v.Add(new("minimize", $"{after.Name}: Win+Down did not minimize it", after.Hwnd));
            if (after.Lie) v.Add(new("minimize", $"{after.Name}: minimized but still fullscreen-in-slot", after.Hwnd));
            return v;
        }
        if (chord == "Win+Up" && !before.Lie)
        {
            if (!after.Zoomed) v.Add(new("maximize", $"{after.Name}: Win+Up did not maximize it", after.Hwnd));
            return v;
        }
        if (want is not { } target) return v;
        if (before.Lie)
        {
            if (!after.Lie) v.Add(new("tile-keeps-fullscreen", $"{after.Name}: after {chord} it is no longer fullscreen-in-slot", after.Hwnd));
            else if (after.Slot is { } s && !Invariants.Near(s, target, 2)) v.Add(new("tile-slot", $"{after.Name}: after {chord} the slot is {s}, expected {target}", after.Hwnd));
        }
        else
        {
            // A normal window: judge the VISIBLE frame when Windows can tell us, else the window rect with room for invisible borders.
            var seen = after.Frame ?? after.Rect;
            var tol = after.Frame is null ? 16 : 2;
            if (!Invariants.Near(seen, target, tol)) v.Add(new("tile-rect", $"{after.Name}: after {chord} it occupies {seen}, expected {target}", after.Hwnd));
        }
        return v;
    }
}

// ---- the key stream an application would see ----

public enum Src { Pass, Injected, Swallowed }   // Pass = the physical key reaching the app, Injected = typed by the manager, Swallowed = a key the manager took for itself (never reaches the app)

public readonly record struct AppKey(long Ticks, int Vk, bool Up, Src Src);

public static class AppView
{
    static bool IsAltOrWin(int vk) => Vk.IsAltOrWin(vk);

    // events: every key the application would see, in order, plus the keys the manager swallowed (they mark a chord the manager took over).
    // initiallyDown / physicalDownAtEnd: keys physically held at the start / end of the window.
    public static List<Violation> Check(IReadOnlyList<AppKey> events, IReadOnlyCollection<int>? initiallyDown = null, IReadOnlyCollection<int>? physicalDownAtEnd = null)
    {
        var v = new List<Violation>();
        var down = new HashSet<int>(initiallyDown ?? []);
        var downSince = new Dictionary<int, int>();   // modifier vk -> index of its press, to look for a mask key in between
        var passDown = new HashSet<int>(down.Where(IsAltOrWin)); // Alt/Win the USER holds (reached the app as a physical key); the manager's own Win chords are not those
        for (var i = 0; i < events.Count; i++)
        {
            var e = events[i];
            if (e.Src == Src.Swallowed) continue;
            if (!e.Up)
            {
                down.Add(e.Vk);
                if (IsAltOrWin(e.Vk)) { downSince[e.Vk] = i; if (e.Src == Src.Pass) passDown.Add(e.Vk); }
                // Alt/Win held in the app's eyes while the manager types an ordinary key: the app gets Alt+Home (navigate back), Win+End...
                if (e.Src == Src.Injected && !Vk.IsModifier(e.Vk) && e.Vk != Vk.Mask && passDown.Count > 0)
                    v.Add(new("output-under-modifier", $"{Chord.NameOf(e.Vk)} was typed while the user's {string.Join("+", passDown.Select(Chord.NameOf))} was still down in the app"));
            }
            else
            {
                passDown.Remove(e.Vk);
                if (!down.Remove(e.Vk) && e.Src == Src.Injected && !Vk.IsModifier(e.Vk)) v.Add(new("release-without-press", $"{Chord.NameOf(e.Vk)} released but never pressed"));
                if (IsAltOrWin(e.Vk) && downSince.Remove(e.Vk, out var from))
                {
                    // Only where the manager took a key for itself while this modifier was down: an unbound chord reaches the app as the real chord.
                    var span = events.Skip(from + 1).Take(i - from - 1).ToList();
                    if (span.Any(x => x.Src == Src.Swallowed) && !span.Any(x => x.Vk == Vk.Mask))
                        v.Add(new("unmasked-release", $"{Chord.NameOf(e.Vk)} was released with no mask key since it went down, after the manager swallowed a key: the app would open its menu / Start"));
                }
            }
        }
        var stuck = down.Where(k => k != Vk.Mask && !(physicalDownAtEnd?.Contains(k) ?? false)).ToList();
        if (stuck.Count > 0) v.Add(new("stuck-key", $"still down in the app after everything was released: {string.Join(", ", stuck.Select(Chord.NameOf))}"));
        var missing = (physicalDownAtEnd ?? []).Where(k => !down.Contains(k) && !Vk.IsModifier(k)).ToList();
        if (missing.Count > 0) v.Add(new("held-key-lost", $"physically held but not down in the app: {string.Join(", ", missing.Select(Chord.NameOf))}"));
        return v;
    }
}

// ---- expectations on what was emitted ----

public static class Emissions
{
    // "tap:Home" -> Home pressed and released (consecutively among non-modifier events). Counts matching taps.
    public static int CountTaps(IReadOnlyList<AppKey> events, int vk)
    {
        var n = 0;
        for (var i = 0; i + 1 < events.Count; i++)
            if (events[i].Vk == vk && !events[i].Up && events[i].Src == Src.Injected && events[i + 1].Vk == vk && events[i + 1].Up) n++;
        return n;
    }
}
