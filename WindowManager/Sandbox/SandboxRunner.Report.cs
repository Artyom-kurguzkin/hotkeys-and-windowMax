using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WindowManager;

// Turns the run's findings and measurements into two files next to the log:
//   report.md       what to read: verdict, environment, failures by rule with evidence, responsiveness, glitches
//   readings.json   everything, for digging
// Written every so often during the run (a run that is killed still leaves readings) and again at the end.
sealed partial class SandboxRunner
{
    public void WriteReports()
    {
        Finding[] fs; TestRec[] ts; ActionRec[] acts;
        lock (findings) { fs = [.. findings]; ts = [.. tests]; acts = [.. actionRecs]; }
        double[] lat; lock (sg) lat = [.. loopLat.Select(x => x.Ms)];
        var md = new StringBuilder();
        var hard = fs.Where(f => f.Sev == "FAIL").ToArray();
        var ux = fs.Where(f => f.Sev == "UX").ToArray();

        md.AppendLine("# Sandbox readings");
        md.AppendLine();
        md.AppendLine($"Run {startedAt:yyyy-MM-dd HH:mm:ss}, {wall.Elapsed.TotalSeconds:F0}s, macro `{Path.GetFileName(macroPath)}`, monitor {caps.Monitor}, work area {caps.Work}.");
        md.AppendLine();
        md.AppendLine("## Verdict");
        md.AppendLine();
        var failed = ts.Count(t => t.Fails > 0); var uxOnly = ts.Count(t => t.Fails == 0 && t.Ux > 0); var skipped = ts.Count(t => t.Skipped is not null);
        md.AppendLine($"- **Tests:** {ts.Length} run, {ts.Count(t => t.Fails == 0 && t.Ux == 0 && t.Skipped is null)} clean, **{failed} with defects**, {uxOnly} with quality-bar misses only, {skipped} skipped.");
        md.AppendLine($"- **Defects (hard):** {hard.Length} findings over {hard.Select(f => f.Rule).Distinct().Count()} rules.");
        md.AppendLine($"- **Quality bar (glitches, lag):** {ux.Length} findings over {ux.Select(f => f.Rule).Distinct().Count()} rules.");
        md.AppendLine($"- **App-side chrome frames** (the app's own title bar for 1 to 2 frames inside its own F11; the manager cannot shorten these): {fs.Count(f => f.Rule == "app-frame")}.");
        md.AppendLine($"- **Actions judged:** {acts.Length}. **Loop probes:** {lat.Length}.");
        md.AppendLine();

        md.AppendLine("## Environment check");
        md.AppendLine();
        md.AppendLine("| what | result |");
        md.AppendLine("|---|---|");
        md.AppendLine($"| monitor lie engaged for the test window | {Yn(caps.Dll)} |");
        md.AppendLine($"| F11 delivered to the window | {Yn(caps.F11Delivery)} |");
        md.AppendLine($"| foreground follows activation | {Yn(caps.ForegroundChain)} |");
        md.AppendLine($"| window events from Windows on this desktop | {Yn(caps.RealWinEvents)} |");
        md.AppendLine($"| window events from the pump | {Yn(caps.SyntheticWinEvents)} |");
        md.AppendLine($"| visible frame bounds (DWM) | {Yn(caps.DwmFrame)} |");
        md.AppendLine($"| UI Automation hint scan | {Yn(caps.Uia)} ({caps.UiaTargets} targets in {caps.UiaScanMs:F0}ms) |");
        md.AppendLine($"| entered fullscreen-in-slot after activation | {caps.ActivateToLieMs:F0}ms |");
        md.AppendLine($"| sleep(1) really takes | {caps.SleepResolutionMs:F1}ms |");
        if (caps.Detail.Length > 0) { md.AppendLine(); md.AppendLine("```"); md.AppendLine(caps.Detail); md.AppendLine("```"); }
        if (caps.Abort is { } ab) { md.AppendLine(); md.AppendLine($"**Run stopped:** {ab}"); }
        md.AppendLine();

        Section(md, "Defects", hard, evidencePerRule: 3, maxBlocks: 45);
        Section(md, "Quality bar misses", ux, evidencePerRule: 2, maxBlocks: 30);

        md.AppendLine("## Responsiveness");
        md.AppendLine();
        if (lat.Length > 0)
        {
            var s = Stats.Summarize(lat);
            md.AppendLine($"Time for a trivial request to reach the manager's loop, probed every 20ms for the whole run: median {s.P50:F1}ms, p95 {s.P95:F1}ms, worst {s.Max:F0}ms; {lat.Count(x => x >= opt["loopSlowMs"])} probes over {opt["loopSlowMs"]:F0}ms.");
            md.AppendLine();
        }
        md.AppendLine("| action | mode | n | first change p50 / p95 / max (ms) | settled p50 / p95 / max (ms) | loop worst (ms) | key waited worst (ms) | F11s avg / max | states seen |");
        md.AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (var g in acts.Where(a => a.SettleMs > 0 || a.FirstChangeMs > 0).GroupBy(a => (a.Label, Mode(a.Test))).OrderBy(g => g.Key.Label).ThenBy(g => g.Key.Item2))
        {
            var fc = Stats.Summarize(g.Select(a => a.FirstChangeMs)); var st = Stats.Summarize(g.Select(a => a.SettleMs));
            md.AppendLine($"| {g.Key.Label} | {g.Key.Item2} | {g.Count()} | {fc.P50:F0} / {fc.P95:F0} / {fc.Max:F0} | {st.P50:F0} / {st.P95:F0} / {st.Max:F0} | {g.Max(a => a.LoopMaxMs):F0} | {g.Max(a => a.KeyQueueMs):F0} | {g.Average(a => a.F11):F1} / {g.Max(a => a.F11)} | {g.Max(a => a.States)} |");
        }
        md.AppendLine();

        md.AppendLine("## Glitches seen (in-between states the user would see)");
        md.AppendLine();
        var glitches = ux.Where(f => f.Rule is "transient-state" or "style-flicker" or "overshoot").ToArray();
        if (glitches.Length == 0) md.AppendLine("None.");
        else foreach (var g in glitches.GroupBy(f => (f.Rule, Regex.Replace(f.Message, @"\d+", "#"))).OrderByDescending(g => g.Count()).Take(15))
            md.AppendLine($"- {g.Count()}x **{g.Key.Rule}**: {g.First().Message}  (e.g. test: {g.First().Test})");
        md.AppendLine();

        md.AppendLine("## Tests");
        md.AppendLine();
        foreach (var grp in ts.GroupBy(t => t.Group))
            md.AppendLine($"- **{(grp.Key.Length == 0 ? "(ungrouped)" : grp.Key)}**: {grp.Count()} tests, {grp.Count(t => t.Fails > 0)} with defects, {grp.Count(t => t.Fails == 0 && t.Ux > 0)} with quality misses, {grp.Count(t => t.Skipped is not null)} skipped, {grp.Sum(t => t.Checks)} explicit checks");
        md.AppendLine();
        var bad = ts.Where(t => t.Fails > 0).Take(120).ToArray();
        if (bad.Length > 0)
        {
            md.AppendLine("Tests with defects:");
            md.AppendLine();
            foreach (var t in bad) md.AppendLine($"- {t.Group} / {t.Name}: {string.Join(", ", t.Rules)}");
            md.AppendLine();
        }
        var skip = ts.Where(t => t.Skipped is not null).ToArray();
        if (skip.Length > 0) { md.AppendLine("Skipped:"); md.AppendLine(); foreach (var t in skip.Take(30)) md.AppendLine($"- {t.Name}: {t.Skipped}"); md.AppendLine(); }

        File.WriteAllText(Path.Combine(outDir, "report.md"), md.ToString());
        File.WriteAllText(Path.Combine(outDir, "readings.json"), JsonSerializer.Serialize(new
        {
            run = new { startedAt, seconds = wall.Elapsed.TotalSeconds, macro = macroPath },
            options = opt, environment = caps,
            tests = ts.Select(t => new { t.Group, t.Name, t.Checks, t.Fails, t.Ux, t.Skipped, t.Ms, t.Rules }),
            actions = acts,
            findings = fs.Select(f => new { f.Sev, f.Rule, f.Message, f.Test, f.Step, f.Evidence }),
            loop = lat.Length == 0 ? null : Stats.Summarize(lat),
        }, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
    }

    static string Yn(bool b) => b ? "yes" : "**NO**";
    static string Mode(string test) => Regex.Match(test, @"\[(\w+)\]") is { Success: true } m ? m.Groups[1].Value : "-";

    static void Section(StringBuilder md, string title, Finding[] list, int evidencePerRule, int maxBlocks)
    {
        md.AppendLine($"## {title}");
        md.AppendLine();
        if (list.Length == 0) { md.AppendLine("None."); md.AppendLine(); return; }
        md.AppendLine("| rule | count | tests affected | first example |");
        md.AppendLine("|---|---|---|---|");
        var groups = list.GroupBy(f => f.Rule).OrderByDescending(g => g.Count()).ToArray();
        foreach (var g in groups)
            md.AppendLine($"| {g.Key} | {g.Count()} | {g.Select(f => f.Test).Distinct().Count()} | {Cell(g.First().Message)} |");
        md.AppendLine();
        var blocks = 0;
        foreach (var g in groups)
        {
            foreach (var f in g.Take(evidencePerRule))
            {
                if (blocks++ >= maxBlocks) break;
                md.AppendLine($"### {g.Key}: {f.Test}");
                md.AppendLine();
                md.AppendLine($"{f.Message}");
                md.AppendLine($"_step: {f.Step}_");
                if (f.Evidence.Length > 0) { md.AppendLine(); md.AppendLine("```"); md.AppendLine(f.Evidence.Length > 3500 ? f.Evidence[..3500] + "\n..." : f.Evidence); md.AppendLine("```"); }
                md.AppendLine();
            }
        }
    }

    static string Cell(string s) { s = s.Replace("|", "/").Replace("\n", " "); return s.Length > 160 ? s[..160] + "..." : s; }
}
