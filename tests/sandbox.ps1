# Runs a test macro against the window manager on a HIDDEN desktop. Nothing on your own desktop is touched:
# the windows, the keys the macro "presses" and the cursor all live on a second desktop that is never shown, and the
# manager there cannot send input to yours (that is built in, not a convention).
#
#   .\tests\sandbox.ps1                              the full macro (about 40 minutes)
#   .\tests\sandbox.ps1 -Macro tests\macros\smoke.macro   a few minutes: environment check + the basics
#   .\tests\sandbox.ps1 -NoBuild                     skip the build (use what is already in bin\Debug)
#
# Read the result in tests\out\sandbox\report.md (verdict, failures by rule with evidence, responsiveness, glitches).
# tests\out\sandbox\readings.json has everything; wm.log is the manager's own log; progress.log shows where it is.
param(
    [string]$Macro = "tests\macros\full.macro",
    [string]$Out = "tests\out\sandbox",
    [int]$TimeoutMinutes = 90,
    [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

$manager = Join-Path $repo "WindowManager\bin\Debug\net10.0-windows\WindowManager.exe"
$fixture = Join-Path $repo "WindowManager.Fixture\bin\Debug\net10.0-windows\WmFixture.exe"
$macroPath = (Resolve-Path $Macro).Path
$outPath = [IO.Path]::GetFullPath((Join-Path $repo $Out))
if (-not $outPath.StartsWith((Join-Path $repo "tests\out"), [StringComparison]::OrdinalIgnoreCase)) { throw "-Out must be under tests\out (it is emptied before every run)" }

# A previous sandbox run still alive would hold the exe open and share nothing safely: stop here instead of guessing.
$alive = Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -and ($_.Path -ieq $manager -or ($_.Path.StartsWith((Split-Path $fixture), [StringComparison]::OrdinalIgnoreCase) -and $_.Name -match '^(WmFixture|WmFx-.*)$')) }  # never the real manager: it runs from dist
if ($alive) { throw "A sandbox run is still going (pid $($alive[0].Id)). Wait for it, or stop it: Stop-Process -Id $($alive[0].Id)" }

if (-not $NoBuild) {
    Write-Host "building..."
    foreach ($proj in "WindowManager", "WindowManager.Fixture") {
        $out = dotnet build $proj -c Debug --nologo -v q 2>&1
        if ($LASTEXITCODE -ne 0) { $out | Where-Object { $_ -match "error" } | Select-Object -First 8 | ForEach-Object { Write-Host $_ }; throw "build failed: $proj" }
    }
}
if (-not (Test-Path $manager)) { throw "not built: $manager" }
if (-not (Test-Path $fixture)) { throw "not built: $fixture" }

if (Test-Path $outPath) { Remove-Item $outPath -Recurse -Force }
New-Item -ItemType Directory -Force $outPath | Out-Null

Add-Type -TypeDefinition @"
using System;using System.Runtime.InteropServices;
public static class Sbx {
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] struct STARTUPINFO { public int cb; public string r, desktop, title; public int x,y,xs,ys,xc,yc,fill,flags; public short show, cbr2; public IntPtr r2, i, o, e; }
  [StructLayout(LayoutKind.Sequential)] struct PROCESS_INFORMATION { public IntPtr hP, hT; public int pid, tid; }
  [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern IntPtr CreateDesktop(string n, string d, IntPtr dm, uint f, uint a, IntPtr sa);
  [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr h);
  [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool CreateProcess(string app, string cmd, IntPtr pa, IntPtr ta, bool inh, uint fl, IntPtr env, string dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
  [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
  public static IntPtr Desktop(string name) {
    var h = CreateDesktop(name, null, IntPtr.Zero, 0, 0x10000000, IntPtr.Zero);
    if (h == IntPtr.Zero) throw new Exception("CreateDesktop failed, error " + Marshal.GetLastWin32Error());
    return h;
  }
  public static void Close(IntPtr h) { CloseDesktop(h); }
  [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);
  // A laptop that falls asleep mid-run ruins the readings (measured: 77 seconds of sleep in the middle of one).
  public static void KeepAwake(bool on) { SetThreadExecutionState(on ? 0x80000001u : 0x80000000u); }
  public static int Start(string exe, string args, string dir, string desktop) {
    var si = new STARTUPINFO(); si.cb = Marshal.SizeOf(typeof(STARTUPINFO)); si.desktop = desktop;
    PROCESS_INFORMATION pi;
    if (!CreateProcess(exe, "\"" + exe + "\" " + args, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, dir, ref si, out pi))
      throw new Exception("CreateProcess failed, error " + Marshal.GetLastWin32Error());
    CloseHandle(pi.hT); CloseHandle(pi.hP);
    return pi.pid;
  }
}
"@

[Sbx]::KeepAwake($true)   # no idle sleep while this runs (closing the lid still sleeps it)
$desktopName = "wmsandbox-$PID"
$desk = [Sbx]::Desktop($desktopName)
$mgrPid = 0
try {
    $args2 = "--sandbox --sandbox-dir `"$outPath`" --macro `"$macroPath`" --fixture `"$fixture`""
    $mgrPid = [Sbx]::Start($manager, $args2, (Split-Path $manager), $desktopName)
    Write-Host "running $(Split-Path $macroPath -Leaf) on hidden desktop '$desktopName' (manager pid $mgrPid). Your own desktop is not touched."
    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    $lastLine = ""
    while ($true) {
        $p = Get-Process -Id $mgrPid -ErrorAction SilentlyContinue
        if (-not $p) { break }
        if ((Get-Date) -gt $deadline) { Write-Host "TIMEOUT after $TimeoutMinutes minutes: stopping it (readings so far are kept)"; break }
        $progress = Join-Path $outPath "progress.log"
        if (Test-Path $progress) {
            $line = (Get-Content $progress -Tail 1 -ErrorAction SilentlyContinue)
            if ($line -and $line -ne $lastLine) { Write-Host $line; $lastLine = $line }
        }
        Start-Sleep -Seconds 3
    }
}
finally {
    # Only what this run started: the manager (its children are the fixtures) and any renamed fixture copy of ours.
    if ($mgrPid -ne 0 -and (Get-Process -Id $mgrPid -ErrorAction SilentlyContinue)) { taskkill /PID $mgrPid /T /F | Out-Null }
    Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($repo, [StringComparison]::OrdinalIgnoreCase) -and $_.Name -match '^(WmFixture|WmFx-.*)$' } | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
    [Sbx]::Close($desk)
    [Sbx]::KeepAwake($false)
}

$report = Join-Path $outPath "report.md"
if (Test-Path $report) {
    Write-Host ""
    Get-Content $report | Select-Object -First 14
    Write-Host ""
    Write-Host "full report: $report"
    Write-Host "everything:  $(Join-Path $outPath 'readings.json')   log: $(Join-Path $outPath 'wm.log')"
} else {
    Write-Host "no report was written; see $(Join-Path $outPath 'wm.log') and progress.log"
}
