# Builds monlie.dll (x64) from monlie.c + vendored MinHook using mingw-w64 gcc.
# The DLL has no Windows SDK dependency; gcc bundles windows.h and the CRT.
# Run this whenever monlie.c changes; the .dll is committed and copied to the build
# output by the .csproj, so a normal `dotnet build` does not need a C compiler.
$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$mh   = Join-Path $here "minhook"

# Find gcc: PATH first, then the WinLibs winget install location.
$gcc = (Get-Command gcc -ErrorAction SilentlyContinue).Source
if (-not $gcc) {
  $gcc = Get-ChildItem "$env:LOCALAPPDATA\Microsoft\WinGet\Packages\BrechtSanders.WinLibs*\mingw64\bin\gcc.exe" -ErrorAction SilentlyContinue |
         Select-Object -First 1 -Expand FullName
}
if (-not $gcc) { throw "gcc not found. Install with: winget install BrechtSanders.WinLibs.POSIX.UCRT" }

$out = Join-Path $here "monlie.dll"
& $gcc -O2 -shared -o $out `
  (Join-Path $here "monlie.c") `
  (Join-Path $mh "src\hook.c") (Join-Path $mh "src\buffer.c") (Join-Path $mh "src\trampoline.c") (Join-Path $mh "src\hde\hde64.c") `
  -I(Join-Path $mh "include") -I(Join-Path $mh "src") -luser32
if ($LASTEXITCODE -ne 0) { throw "gcc failed ($LASTEXITCODE)" }
Write-Host "built $out"
