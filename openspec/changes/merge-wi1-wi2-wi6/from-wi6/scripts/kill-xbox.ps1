# Permanent, machine-level disable of Xbox Game Bar + Edge Game Assist (needs admin).
$log = "C:\Users\artyo\AppData\Local\Temp\kill-xbox.log"
"START $(Get-Date)" | Set-Content $log
function Log($m){ $m | Add-Content $log }

# 1) Machine policy: fully disable Windows Game Recording/Broadcasting (Game Bar/DVR)
try {
  New-Item -Path "HKLM:\SOFTWARE\Policies\Microsoft\Windows\GameDVR" -Force | Out-Null
  New-ItemProperty -Path "HKLM:\SOFTWARE\Policies\Microsoft\Windows\GameDVR" -Name "AllowGameDVR" -Value 0 -PropertyType DWord -Force | Out-Null
  Log "OK HKLM AllowGameDVR=0"
} catch { Log "FAIL AllowGameDVR: $($_.Exception.Message)" }

# 2) Machine policy: disable Edge Game Assist so it cannot re-enable
try {
  New-Item -Path "HKLM:\SOFTWARE\Policies\Microsoft\Edge" -Force | Out-Null
  New-ItemProperty -Path "HKLM:\SOFTWARE\Policies\Microsoft\Edge" -Name "GameAssistEnabled" -Value 0 -PropertyType DWord -Force | Out-Null
  Log "OK HKLM Edge GameAssistEnabled=0"
} catch { Log "FAIL Edge policy: $($_.Exception.Message)" }

# 3) Remove the PROVISIONED packages so they don't reinstall on update / for new users
#    (GamingApp -> XboxPcAppCE is what launches on Win+Up / game detection; SpeechToTextOverlay is another overlay)
foreach ($name in 'Microsoft.XboxGamingOverlay','Microsoft.Edge.GameAssist','Microsoft.GamingApp','Microsoft.XboxSpeechToTextOverlay') {
  try {
    $p = Get-AppxProvisionedPackage -Online | Where-Object DisplayName -eq $name
    if ($p) { Remove-AppxProvisionedPackage -Online -PackageName $p.PackageName -ErrorAction Stop | Out-Null; Log "OK deprovisioned $name" }
    else { Log "note: $name not provisioned (already gone)" }
  } catch { Log "FAIL deprovision ${name}: $($_.Exception.Message)" }
}

# 4) Also remove the per-user installs (all users) in case they returned
foreach ($name in 'Microsoft.XboxGamingOverlay','Microsoft.Edge.GameAssist','Microsoft.GamingApp','Microsoft.XboxSpeechToTextOverlay') {
  try { Get-AppxPackage $name -AllUsers | Remove-AppxPackage -AllUsers -ErrorAction Stop; Log "OK removed $name (all users)" } catch { Log "note: remove ${name} all-users: $($_.Exception.Message)" }
}
# 5) Kill the game-detection app if running
Get-Process -Name "XboxPcApp*","GamingApp*" -ErrorAction SilentlyContinue | ForEach-Object { try { Stop-Process -Id $_.Id -Force; Log "stopped $($_.Name)" } catch {} }

"DONE $(Get-Date)" | Add-Content $log
