using System.Diagnostics;

namespace WindowManager;

/// <summary>What the swap installs: the staged release, the install folder, and the exe to start afterwards (null = only swap).</summary>
public sealed record UpdateApplication(string Staged, string Install, string? RestartExe);

/// <summary>
/// Swaps a staged release into the install folder from inside the running app. Windows lets a running exe or a dll other processes still map be renamed, though
/// not overwritten, so every file the release replaces is renamed aside (name.&lt;ticks&gt;.old) and the new one copied in; a failure puts the old files back.
/// The new exe is then started, and as a new instance it closes this one (Program.ReplaceRunningInstances). The .old files are swept by <see cref="Sweep"/>
/// once they unlock. ponytail: no separate helper process or script — a hidden PowerShell with -ExecutionPolicy Bypass got the exe quarantined by
/// Defender (Trojan:Win32/Bearfoos.A!ml, 2026-11-26).
/// </summary>
public static class UpdateHelper
{
    /// <summary>Installs the staged files and starts the new exe; false (and the old files back in place) when the swap failed.</summary>
    public static bool Apply(UpdateApplication apply)
    {
        Sweep(apply.Install);
        var done = new List<(string Target, string? Aside)>();
        var ticks = DateTime.Now.Ticks;
        try
        {
            foreach (var source in Directory.EnumerateFiles(apply.Staged, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(apply.Install, Path.GetRelativePath(apply.Staged, source));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                string? aside = null;
                if (File.Exists(target)) { aside = $"{target}.{ticks}.old"; File.Move(target, aside); }
                done.Add((target, aside));
                File.Copy(source, target);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Info($"update: the swap failed ({e.Message}); putting the old files back");
            foreach (var (target, aside) in Enumerable.Reverse(done))
                try { if (File.Exists(target)) File.Delete(target); if (aside is not null) File.Move(aside, target); }
                catch (Exception e2) when (e2 is IOException or UnauthorizedAccessException) { Log.Info($"update: could not restore {target}: {e2.Message}"); }
            return false;
        }
        Log.Info($"update: installed {apply.Staged} into {apply.Install} ({done.Count} files)");
        if (apply.RestartExe is not null)
            try { Process.Start(new ProcessStartInfo(apply.RestartExe) { UseShellExecute = true, WorkingDirectory = apply.Install }); }
            catch (Exception e) { Log.Info($"update: could not start {apply.RestartExe}: {e.Message}"); }
        return true;
    }

    /// <summary>Deletes the *.old files earlier swaps (and builds) left in the folder; the ones still locked stay for the next time.</summary>
    public static void Sweep(string install)
    {
        try
        {
            foreach (var old in Directory.EnumerateFiles(install, "*.old", SearchOption.AllDirectories))
                try { File.Delete(old); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
