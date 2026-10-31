$log = "C:\Users\artyo\AppData\Local\Temp\procmon.log"
"START $(Get-Date)  -- press Win+Up now --" | Set-Content $log
$seen = @{}; Get-Process | ForEach-Object { $seen[$_.Id] = $true }
$end = (Get-Date).AddSeconds(45)
while((Get-Date) -lt $end){
  foreach($p in Get-Process){
    if(-not $seen.ContainsKey($p.Id)){
      $seen[$p.Id] = $true
      $cl = (Get-CimInstance Win32_Process -Filter "ProcessId=$($p.Id)" -EA SilentlyContinue).CommandLine
      ("{0}  NEW {1} (pid {2})  {3}" -f (Get-Date -Format HH:mm:ss), $p.Name, $p.Id, $cl) | Add-Content $log
    }
  }
  Start-Sleep -Milliseconds 250
}
"END $(Get-Date)" | Add-Content $log
