# Automated regression of every scriptable case in openspec/changes/add-monitor-lie/TESTS.md.
# Usage:  . .\tests\wm.ps1 ; .\tests\scenarios.ps1 [-Proc Code] [-NoCycle]
param([string]$Proc = 'msedge', [switch]$NoCycle)
. (Join-Path $PSScriptRoot "wm.ps1")
if(-not $NoCycle){ Cycle }

# Known starting state: un-minimized, managed, active (the previous run may have left it minimized).
$h = Prepare $Proc
$w = WinOf $h
"target: $($w.proc) $($w.hwnd) '$($w.title)'"

# A1: becomes borderless fullscreen when activated
Activate $h; Start-Sleep -Seconds 2
Check "A1 activate -> fullscreen into slot"   { $s=WinOf $h; $s.lie -and $s.fillsSlot }
Check "A1 foreground is the target"           { (WinOf $h).foreground }

# B: tiling via the REAL key path
Key 'Win+Left';  Start-Sleep -Seconds 2
Check "B1 Win+Left -> left half, fills slot"  { $s=WinOf $h; $s.slot -match '^\(0,0 ' -and $s.fillsSlot }
Key 'Win+Right'; Start-Sleep -Seconds 2
Check "B2 Win+Right -> right half, fills slot"{ $s=WinOf $h; $s.slot -notmatch '^\(0,0 ' -and $s.fillsSlot }
Key 'Win+Up';    Start-Sleep -Seconds 2
Check "B3 Win+Up -> full monitor, fills slot"  { $s=WinOf $h; $s.fillsSlot -and $s.rect -eq $s.slot }
Check "B3 Win+Up launched nothing Xbox"       { -not (Get-Process | Where-Object { $_.Name -match 'Xbox|GameBar|GamingApp|EdgeGameAssist' }) }
Key 'Win+Left'; Key 'Win+Right'; Key 'Win+Up'; Start-Sleep -Seconds 2
Check "K1 rapid L/R/Up ends full, no desync"  { $s=WinOf $h; $s.lie -and $s.fillsSlot -and $s.rect -eq $s.slot }

# C: reveal via the real Win+Alt tap
Tap 'Win+Alt';   Start-Sleep -Seconds 2
Check "C1 Win+Alt tap -> revealed, not lie"   { $s=WinOf $h; $s.revealed -and -not $s.lie }
Tap 'Win+Alt';   Start-Sleep -Seconds 2
Check "C2 Win+Alt again -> back in slot"      { $s=WinOf $h; $s.lie -and $s.fillsSlot -and -not $s.revealed }

# C3: Alt+T
Key 'Alt+T';     Start-Sleep -Seconds 2
Check "C3 Alt+T -> revealed"                  { $s=WinOf $h; $s.revealed -and -not $s.lie }
Key 'Alt+T';     Start-Sleep -Seconds 2
Check "C3 Alt+T again -> back in slot"        { $s=WinOf $h; $s.lie -and $s.fillsSlot }

# G1: triple-Alt highlight (observable in the log)
$t=Get-Date; TripleAlt; Start-Sleep -Milliseconds 800
Check "G1 triple-Alt -> highlight logged"     { [bool](LogSince $t 'highlight') }

# B4: Win+Down minimizes, restore re-enters
Key 'Win+Down';  Start-Sleep -Seconds 1
Check "B4 Win+Down -> iconic"                 { (WinOf $h).iconic }
Activate $h; Start-Sleep -Seconds 2
Check "D5 restore -> back in slot"            { $s=WinOf $h; $s.lie -and $s.fillsSlot }

# H: hint mode through the real key path - Alt+F labels every clickable thing, Esc hides. (The pick itself really
# clicks something, so it is exercised by hand: Alt+F, then a label.)
Key 'Alt+F'; Start-Sleep -Seconds 2
Check "H1 Alt+F -> hints shown with targets"      { $s=(State).hints; $s.active -and $s.targets -gt 0 }
Key 'Esc'; Start-Sleep -Milliseconds 600
Check "H2 Esc -> hints hidden"                { -not (State).hints.active }

# Self-check: no lingering mismatches
Check "invariants: no MISMATCH episodes"      { -not (Mismatches) }

Report
