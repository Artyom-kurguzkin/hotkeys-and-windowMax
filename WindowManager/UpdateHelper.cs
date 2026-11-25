using System.Diagnostics;

namespace WindowManager;

/// <summary>What the helper swaps: the staged release, the install folder, the folder that keeps the old copy, the process to wait for, and the exe to start afterwards (null = only swap).</summary>
public sealed record UpdateApplication(string Staged, string Install, string Backup, int ProcessId, string? RestartExe);

/// <summary>
/// The small PowerShell script that replaces the app's files, as repo-task-manager's UpdateHelper: an exe cannot replace itself while it runs, so the app starts the script and the script waits
/// for the app's process to end, keeps the old files in the backup folder (one rollback: the folder is replaced by the next update), copies the staged release over the install folder (files the
/// release does not hold stay), puts the old files back when the copy fails, and starts the app again when asked. One addition for this app: monlie.dll stays mapped in other processes after the
/// app quits, so the old copy is renamed aside (monlie.&lt;ticks&gt;.old, which Windows allows for a mapped file, as the build does) before the new one is copied, and old .old files are swept
/// when they have unlocked. Everything it does is written to its log.
/// </summary>
public static class UpdateHelper
{
    const string Script = """
        param([int]$ProcessId, [string]$Staged, [string]$Install, [string]$Backup, [string]$Log, [string]$Restart = '', [int]$Retries = 30)
        $ErrorActionPreference = 'Stop'
        function Say($text) { Add-Content -LiteralPath $Log -Value ('{0:s} {1}' -f (Get-Date), $text) }
        function Copy-Tree($from, $to, $same = @()) { & robocopy.exe $from $to /E @same /XF '*.old' /R:$Retries /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null; return $LASTEXITCODE }   # 0-7 are success codes, 8 and up are failures
        try {
            Say "Waiting for process $ProcessId to exit"
            Wait-Process -Id $ProcessId -Timeout 120 -ErrorAction SilentlyContinue
            if (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) { Say "The app is still running after 120 seconds; nothing was changed"; exit 2 }
            if (Test-Path -LiteralPath $Backup) { Remove-Item -LiteralPath $Backup -Recurse -Force }
            Say "Keeping the current files in $Backup"
            if ((Copy-Tree $Install $Backup) -ge 8) { Say "The backup could not be made; nothing was changed"; exit 3 }
            Get-ChildItem -LiteralPath $Install -Filter '*.old' -ErrorAction SilentlyContinue | ForEach-Object { try { Remove-Item -LiteralPath $_.FullName -Force } catch { Say "$($_.Name) is still in use; left for the next update" } }
            $hook = Join-Path $Install 'monlie.dll'
            if (Test-Path -LiteralPath $hook) {
                $aside = 'monlie.{0}.old' -f (Get-Date).Ticks
                try { Rename-Item -LiteralPath $hook -NewName $aside; Say "Moved the hook dll aside as $aside" } catch { Say "The hook dll could not be moved aside: $($_.Exception.Message)" }
            }
            Say "Copying $Staged to $Install"
            if ((Copy-Tree $Staged $Install @('/IS')) -ge 8) {   # /IS: a file that looks the same (size and time) is copied too
                Say "The copy failed; the old files are restored from $Backup"
                $code = Copy-Tree $Backup $Install @('/IS')   # every file again: robocopy takes times within 2 seconds as equal
                Say "Restored the old files from $Backup (robocopy code $code; a file that was never replaced may still be locked)"
                exit 4
            }
            Say "Updated"
            if ($Restart) { Say "Starting $Restart"; Start-Process -FilePath $Restart -WorkingDirectory $Install }
        } catch { Say "The update failed: $($_.Exception.Message)"; exit 5 }
        """;

    /// <summary>Writes the script beside <paramref name="log"/> and starts it hidden and detached from this process; null when it could not be started. <paramref name="retries"/> is how often a locked file is tried again (once a second).</summary>
    public static Process? Launch(UpdateApplication apply, string log, int retries = 30)
    {
        try
        {
            var folder = Path.GetDirectoryName(Path.GetFullPath(log))!;
            Directory.CreateDirectory(folder);
            var script = Path.Combine(folder, "apply-update.ps1");
            File.WriteAllText(script, Script, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true)); // the BOM: Windows PowerShell reads a file without one as ANSI
            ProcessStartInfo start = new("powershell.exe")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                ArgumentList = { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script,
                    "-ProcessId", apply.ProcessId.ToString(), "-Staged", apply.Staged, "-Install", apply.Install, "-Backup", apply.Backup, "-Log", log, "-Retries", retries.ToString() },
            };
            if (apply.RestartExe is not null) { start.ArgumentList.Add("-Restart"); start.ArgumentList.Add(apply.RestartExe); }
            var process = Process.Start(start);
            Log.Info($"update: started the helper (pid {process?.Id}) to swap {apply.Staged} into {apply.Install} after process {apply.ProcessId} exits; log {log}");
            return process;
        }
        catch (Exception e)
        {
            Log.Info($"update: could not start the helper: {e.Message}");
            return null;
        }
    }
}
