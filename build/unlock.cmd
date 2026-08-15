@echo off
setlocal EnableExtensions

REM ===========================================================================
REM  unlock.cmd - release every handle on stage\
REM
REM  Four separate things can hold files there, and hitting them one at a time
REM  cost several rounds of debugging:
REM
REM    1. The Host, a Worker, or ffmpeg still running
REM    2. The COM surrogate (dllhost.exe) holding the shell DLL, because the
REM       sparse package registration points at stage\
REM    3. An Explorer window displaying the folder
REM    4. signtool mid-write
REM
REM  This handles 1 and 2. If it still fails, close any Explorer window sitting
REM  in the project folder or its stage subfolder - Explorer holds a handle on
REM  whatever it is displaying.
REM ===========================================================================

echo --- Unlocking stage ---

REM  Paths reach PowerShell through the environment. Interpolating them
REM  into a quoted literal breaks on a directory named O'Brien.
set "HERE=%~dp0"

call "%~dp0stop-host.cmd"

REM The sparse package registration keeps the shell DLL loaded in a surrogate.
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$p = Get-AppxPackage -Name 'Jalyro.Convert';" ^
  "if ($p) { foreach ($x in $p) { Remove-AppxPackage -Package $x.PackageFullName -ErrorAction SilentlyContinue };" ^
  "          Write-Host '[ok] Package unregistered.' }" ^
  "else { Write-Host '[ok] Package not registered.' }"

REM  Only the surrogate holding OUR shell DLL. Killing every dllhost.exe in
REM  the session also takes down unrelated thumbnail and preview handlers.
REM  If a surrogate cannot be inspected it is left alone; the check below
REM  then names it rather than this script killing it blind.
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$stage = (Resolve-Path (Join-Path $env:HERE '..\stage') -ErrorAction SilentlyContinue).Path;" ^
  "if (-not $stage) { exit 0 };" ^
  "foreach ($d in @(Get-Process -Name dllhost -ErrorAction SilentlyContinue)) {" ^
  "  $mine = $false;" ^
  "  try { $mine = @($d.Modules | Where-Object {" ^
  "    $_.FileName -like ($stage + '\*') }).Count -gt 0 } catch { };" ^
  "  if ($mine) {" ^
  "    Write-Host ('Stopping surrogate (pid ' + $d.Id + ')');" ^
  "    Stop-Process -Id $d.Id -Force -ErrorAction SilentlyContinue" ^
  "  }" ^
  "}"

REM  ping, not timeout: timeout reads the console input buffer and
REM  silently eats commands pasted after this script.
ping -n 2 127.0.0.1 >nul

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$stage = (Resolve-Path (Join-Path $env:HERE '..\stage') -ErrorAction SilentlyContinue).Path;" ^
  "if (-not $stage) { Write-Host '[ok] No stage directory yet.'; exit 0 };" ^
  "$held = Get-Process | Where-Object { $_.Modules | Where-Object { $_.FileName -like ($stage + '\*') } };" ^
  "if ($held) { Write-Host '[WARN] Still holding files:' -ForegroundColor Yellow;" ^
  "             $held | Select-Object Id,Name,Path | Format-Table | Out-String | Write-Host }" ^
  "else { Write-Host '[ok] Nothing holding stage.' }"

exit /b 0
