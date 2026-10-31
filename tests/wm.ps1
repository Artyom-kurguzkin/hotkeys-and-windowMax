# Test & troubleshooting harness for WindowManager. Dot-source it:  . .\tests\wm.ps1
#
# Drives the manager through its test channel (WM_APP_TESTKEY / DUMPSTATE / ACTIVATE), which runs the SAME code
# path a physical key takes. Needed because the LL keyboard hook drops injected keys and a background script
# cannot set foreground. Everything a test asserts on comes from `State` (JSON the manager writes about itself).
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type @"
using System;using System.Runtime.InteropServices;using System.Text;
public class WM{
 [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr p,IntPtr a,string c,string n);
 [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h,uint m,IntPtr w,IntPtr l);
 [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")] public static extern bool EnumWindows(E cb,IntPtr p); public delegate bool E(IntPtr h,IntPtr p);
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr h);
 [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h,StringBuilder s,int n);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
 [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h,int c);
 [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
}
"@
[void][WM]::SetProcessDPIAware()

$script:Repo   = Split-Path -Parent $PSScriptRoot
$script:Exe    = Join-Path $script:Repo "WindowManager\bin\Debug\net10.0-windows\WindowManager.exe"
$script:LogPath= Join-Path $env:LOCALAPPDATA "WindowManager\wm.log"
$script:State  = Join-Path ([IO.Path]::GetTempPath()) "wm-state.json"
$script:ShotDir= Join-Path $env:USERPROFILE "Pictures\wm-tests"
$script:RunStart = Get-Date   # evidence (log lines, MISMATCHes) is only counted from here; Cycle resets it
$HWND_MESSAGE  = [IntPtr](-3)
$M = @{ TESTKEY=0x800B; DUMPSTATE=0x800C; ACTIVATE=0x800D; SNAP=0x8008; REVEAL=0x8002; CHROME=0x8001; HILITE=0x8006; LAYOUTS=0x800A }
$VK = @{ Win=0x5B; Alt=0xA4; RAlt=0xA5; Ctrl=0xA2; Shift=0xA0; Up=0x26; Down=0x28; Left=0x25; Right=0x27; F11=0x7A; Esc=0x1B; PgUp=0x21; PgDn=0x22 }

function Mgr { [WM]::FindWindowEx($HWND_MESSAGE,[IntPtr]::Zero,"WindowManagerMsg",$null) }
function Post([int]$msg,[int64]$w=0,[int64]$l=0){ $h=Mgr; if($h -eq [IntPtr]::Zero){ throw "manager not running" }; [void][WM]::PostMessage($h,[uint32]$msg,[IntPtr]$w,[IntPtr]$l) }

function VkOf([string]$k){ if($VK.ContainsKey($k)){ return $VK[$k] }; if($k.Length -eq 1){ return [int][char]$k.ToUpper() }; throw "unknown key '$k'" }
function KeyDown([string]$k){ Post $M.TESTKEY (VkOf $k) 1; Start-Sleep -Milliseconds 25 }
function KeyUp([string]$k){ Post $M.TESTKEY (VkOf $k) 0; Start-Sleep -Milliseconds 25 }

# Key "Win+Up" : modifiers down in order, key down/up, modifiers up in reverse - a real chord.
function Key([string]$chord){
  $parts = $chord -split '\+'
  foreach($p in $parts){ KeyDown $p }
  [array]::Reverse($parts); foreach($p in $parts){ KeyUp $p }
}
# Tap "Win+Alt" : hold both, release (fires the modifier-only tap, e.g. reveal / snap layouts).
function Tap([string]$chord){ Key $chord }
# TripleAlt / BothAlts : the gesture shortcuts.
function TripleAlt { 1..3 | ForEach-Object { KeyDown Alt; KeyUp Alt; Start-Sleep -Milliseconds 80 } }
function BothAlts  { KeyDown Alt; KeyDown RAlt; KeyUp RAlt; KeyUp Alt }

# Activate a window via the manager (it owns the input hook and unlocks the foreground lock with a mask-key tap).
# Verified against State and retried; throws if the target never becomes foreground, so a scenario fails FAST with
# "could not activate" instead of every later assertion silently hitting whatever window was foreground.
function Activate([int64]$hwnd){
  for($i=0;$i -lt 5;$i++){
    Post $M.ACTIVATE $hwnd; Start-Sleep -Milliseconds 500
    if([int64](State).foreground -eq $hwnd){ return $true }
  }
  throw ("could not activate 0x{0:X} (foreground stays {1})" -f $hwnd, (State).foreground)
}

# State : what the manager believes + what Windows says, per managed window.
# Each dump echoes the sequence number it was asked with. The file's timestamp was useless: a dump landing in the
# same millisecond as the request read as "older" and cost the whole 3s deadline (measured as a phantom 3s "loop
# stall"). Throws when the manager never answers: a stalled loop fails loud instead of returning stale state.
$script:Seq = 0
function State([int]$waitMs=0){
  $seq = ++$script:Seq; Post $M.DUMPSTATE $seq; if($waitMs){ Start-Sleep -Milliseconds $waitMs }
  $deadline=(Get-Date).AddSeconds(3)
  while((Get-Date) -lt $deadline){
    try { $s = Get-Content $script:State -Raw -ErrorAction Stop | ConvertFrom-Json; if($s.seq -eq $seq){ return $s } } catch {}
    Start-Sleep -Milliseconds 10
  }
  throw "State: no dump $seq within 3s (manager loop stalled?)"
}
function Win([string]$proc){ (State).windows | Where-Object proc -eq $proc | Select-Object -First 1 }
function WinOf([int64]$hwnd){ (State).windows | Where-Object { [int64]$_.hwnd -eq $hwnd } | Select-Object -First 1 }

# Every visible, titled top-level window straight from Windows (minimized ones included - IsWindowVisible is true
# for them), with its process name. Independent of the manager, so it also sees what the manager does NOT manage.
function Windows {
  $rows = New-Object System.Collections.ArrayList
  $cb = [WM+E]{ param($h,$p)
    if([WM]::IsWindowVisible($h) -and [WM]::GetWindowTextLength($h) -gt 0){
      $pp=0; [void][WM]::GetWindowThreadProcessId($h,[ref]$pp)
      try { $pn=(Get-Process -Id $pp -EA Stop).ProcessName } catch { $pn='?' }
      $sb=New-Object System.Text.StringBuilder 120; [void][WM]::GetWindowText($h,$sb,120)
      [void]$rows.Add([pscustomobject]@{ h=[int64]$h; proc=$pn; title=$sb.ToString(); iconic=[WM]::IsIconic($h) })
    }; $true }
  [void][WM]::EnumWindows($cb,[IntPtr]::Zero); $rows
}

# A process's top-level window straight from Windows - including a MINIMIZED one, which the manager does not manage
# until it next moves, so it is invisible to State. Use Prepare to turn it into a managed, active target.
function RawWin([string]$proc){ (Windows | Where-Object proc -eq $proc | Select-Object -First 1).h }

# Prepare 'msedge' : un-minimize the window, wait until the manager has picked it up, activate it; returns its hwnd.
# A scenario must start from a known state: the previous run can leave the target minimized (B4 does), and then
# `Win $proc` finds nothing and every later step is meaningless.
function Prepare([string]$proc){
  $h = RawWin $proc
  if(-not $h){ throw "no window for process '$proc' - open one first" }
  if([WM]::IsIconic([IntPtr]$h)){ [void][WM]::ShowWindow([IntPtr]$h, 9); Start-Sleep -Milliseconds 600 }
  $d=(Get-Date).AddSeconds(6); while(((Get-Date) -lt $d) -and -not (WinOf $h)){ Start-Sleep -Milliseconds 300 }
  if(-not (WinOf $h)){ throw ("window 0x{0:X} of '{1}' never became managed" -f $h, $proc) }
  Activate $h | Out-Null
  $h
}

# Log lines written since a timestamp (default: last 15s).
function LogSince([datetime]$since=(Get-Date).AddSeconds(-15),[string]$match='.'){
  Get-Content $script:LogPath -EA SilentlyContinue | ForEach-Object {
    if($_ -match '^(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d+)'){ $ts=[datetime]$Matches[1]; if($ts -ge $since -and $_ -match $match){ $_ } }
  }
}
# Only THIS run's evidence: a flat look-back counted a previous run's MISMATCH against a clean one.
function Mismatches { LogSince $script:RunStart 'MISMATCH' }

# Diff { action } : every NEW process and NEW top-level window that appeared during the action (catches overlays).
function Diff([scriptblock]$action){
  $p0=@{}; Get-Process | ForEach-Object { $p0[$_.Id]=1 }
  $w0=@{}; $cb=[WM+E]{ param($h,$x) $w0[[int64]$h]=1; $true }; [void][WM]::EnumWindows($cb,[IntPtr]::Zero)
  & $action; Start-Sleep -Seconds 2
  "--- new processes ---"
  Get-Process | Where-Object { -not $p0.ContainsKey($_.Id) } | ForEach-Object { "  {0} (pid {1})" -f $_.Name,$_.Id }
  "--- new visible windows ---"
  $cb2=[WM+E]{ param($h,$x) if(-not $w0.ContainsKey([int64]$h) -and [WM]::IsWindowVisible($h)){ $pp=0;[void][WM]::GetWindowThreadProcessId($h,[ref]$pp); $pn=try{(Get-Process -Id $pp -EA Stop).ProcessName}catch{'?'}; $sb=New-Object Text.StringBuilder 80;[void][WM]::GetWindowText($h,$sb,80); Write-Host ("  {0} 0x{1:X} '{2}'" -f $pn,[int64]$h,$sb) }; $true }
  [void][WM]::EnumWindows($cb2,[IntPtr]::Zero)
}

function Shot([string]$name){
  New-Item -ItemType Directory -Force $script:ShotDir | Out-Null
  $b=[System.Windows.Forms.SystemInformation]::VirtualScreen
  $bmp=New-Object System.Drawing.Bitmap $b.Width,$b.Height; $g=[System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($b.Left,$b.Top,0,0,$bmp.Size); $p=Join-Path $script:ShotDir ($name+".png"); $bmp.Save($p); $g.Dispose(); $bmp.Dispose(); $p
}

# Cycle : close the running manager, rebuild, relaunch, wait until its hook is up. One command per iteration.
function Cycle([switch]$NoBuild){
  for($w=[WM]::FindWindowEx($HWND_MESSAGE,[IntPtr]::Zero,"WindowManagerMsg",$null); $w -ne [IntPtr]::Zero; $w=[WM]::FindWindowEx($HWND_MESSAGE,$w,"WindowManagerMsg",$null)){ [void][WM]::PostMessage($w,0x0010,[IntPtr]::Zero,[IntPtr]::Zero) }
  $d=(Get-Date).AddSeconds(10); while((Get-Process WindowManager -EA SilentlyContinue) -and (Get-Date) -lt $d){ Start-Sleep -Milliseconds 200 }
  if(-not $NoBuild){ $out = dotnet build (Join-Path $script:Repo "WindowManager\WindowManager.csproj") -c Debug 2>&1; if($LASTEXITCODE -ne 0){ $out | Select-Object -Last 15; throw "build failed" } }
  $t0=Get-Date; $script:RunStart=$t0; Start-Process $script:Exe
  $d=(Get-Date).AddSeconds(15); while((Get-Date) -lt $d){ if(LogSince $t0 'monitor-lie hook installed'){ "manager up (pid $((Get-Process WindowManager).Id))"; return }; Start-Sleep -Milliseconds 200 }
  throw "manager did not come up"
}

# Scenario runner: Check "name" { assertion-returning-bool }  -> PASS/FAIL table in $script:Results
$script:Results = New-Object System.Collections.ArrayList
function Check([string]$name,[scriptblock]$test){
  try { $ok = [bool](& $test); $why='' } catch { $ok=$false; $why=$_.Exception.Message }
  [void]$script:Results.Add([pscustomobject]@{ result = $(if($ok){'PASS'}else{'FAIL'}); name=$name; note=$why })
  Write-Host ("{0}  {1}" -f $(if($ok){'PASS'}else{'FAIL'}), $name) -ForegroundColor $(if($ok){'Green'}else{'Red'})
}
function Report { $script:Results | Format-Table -AutoSize; "`n$(($script:Results|? result -eq 'PASS').Count) passed, $(($script:Results|? result -eq 'FAIL').Count) failed"; "MISMATCH lines: $((Mismatches | Measure-Object).Count)" }

Write-Host "wm harness loaded. Key 'Win+Up' | Tap 'Win+Alt' | Activate <hwnd> | State | Win 'Code' | Cycle | Diff {..} | Check | Report"
