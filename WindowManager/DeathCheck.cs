using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;

namespace WindowManager;

/// <summary>
/// Why did the last run end? A killed process (Defender, Task Manager, a native crash) writes nothing, so each run leaves a marker (pid, start time,
/// exe) that a clean exit deletes. The next start finds a marker whose process is gone, logs "previous run ... ended without a clean exit", and looks up
/// the cause in the Windows event logs: a Defender detection of the exe (Windows Defender/Operational 1116/1117), an Application Error (1000) or a
/// .NET Runtime crash (1026) since that start. Defender killing it twice (2026-11-26) left nothing in wm.log; this is what would have said so.
/// </summary>
public static class DeathCheck
{
    public static string MarkerPath => Path.Combine(Log.DefaultDir, "running.txt");

    /// <summary>At startup, before this run's marker is written: logs how the previous run ended when it did not exit cleanly. The event-log reads run in the background.</summary>
    public static void CheckPrevious()
    {
        string[] lines;
        try { if (!File.Exists(MarkerPath)) return; lines = File.ReadAllLines(MarkerPath); }
        catch (Exception e) { Log.Info($"death check: could not read {MarkerPath}: {e.Message}"); return; }
        if (lines.Length < 3 || !int.TryParse(lines[0], out var pid) || !long.TryParse(lines[1], out var ticks)) return;
        var started = new DateTime(ticks, DateTimeKind.Local); var exe = lines[2];
        if (Alive(pid, started)) return; // still running: ReplaceRunningInstances closes it cleanly
        Log.Info($"death check: previous run pid={pid} (started {started:yyyy-MM-dd HH:mm:ss}, {exe}) ended without a clean exit; looking for the cause");
        new Thread(() =>
        {
            var found = Causes(pid, started, exe);
            if (found.Count == 0) Log.Info("death check: no Defender detection or crash event names it: killed from outside (Task Manager, logoff, power loss) or the logs are not readable");
            foreach (var f in found) Log.Info("death check: " + f);
        }) { IsBackground = true, Name = "death-check" }.Start();
    }

    /// <summary>This run is live: a clean exit deletes it (<see cref="Clear"/>).</summary>
    public static void Mark()
    {
        try
        {
            var me = Process.GetCurrentProcess();
            File.WriteAllLines(MarkerPath, [me.Id.ToString(), me.StartTime.Ticks.ToString(), Environment.ProcessPath ?? ""]);
        }
        catch (Exception e) { Log.Info($"death check: could not write {MarkerPath}: {e.Message}"); }
    }

    public static void Clear() { try { File.Delete(MarkerPath); } catch { } }

    static bool Alive(int pid, DateTime started)
    {
        try { using var p = Process.GetProcessById(pid); return Math.Abs((p.StartTime - started).TotalSeconds) < 2; } // a reused pid is another process
        catch { return false; }
    }

    /// <summary>Event-log entries since <paramref name="since"/> that name the exe or the pid.</summary>
    public static List<string> Causes(int pid, DateTime since, string exe)
    {
        var name = Path.GetFileName(exe);
        var from = since.ToUniversalTime().ToString("o");
        var found = new List<string>();
        void Read(string log, string ids, string what)
        {
            try
            {
                var query = new EventLogQuery(log, PathType.LogName, $"*[System[({ids}) and TimeCreated[@SystemTime>='{from}']]]");
                using var reader = new EventLogReader(query);
                for (var e = reader.ReadEvent(); e is not null; e = reader.ReadEvent())
                    using (e)
                    {
                        string text;
                        try { text = e.FormatDescription() ?? string.Join(" | ", e.Properties.Select(p => p.Value)); }
                        catch { text = string.Join(" | ", e.Properties.Select(p => p.Value)); }
                        if (!text.Contains(name, StringComparison.OrdinalIgnoreCase) && !text.Contains($"pid:{pid}", StringComparison.OrdinalIgnoreCase)) continue;
                        found.Add($"{what} at {e.TimeCreated:yyyy-MM-dd HH:mm:ss} (event {e.Id}): {Squash(text)}");
                    }
            }
            catch (Exception ex) { found.Add($"could not read {log}: {ex.Message}"); }
        }
        Read("Microsoft-Windows-Windows Defender/Operational", "EventID=1116 or EventID=1117", "Defender");
        Read("Application", "EventID=1000 or EventID=1026", "crash");
        return found;
    }

    static string Squash(string s)
    {
        s = string.Join(" ", s.Split((char[])['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()));
        return s.Length > 600 ? s[..600] + "…" : s;
    }
}
