@echo off
setlocal EnableExtensions

REM ===========================================================================
REM  stop-host.cmd - stop everything of ours that could hold a file open
REM
REM  Uses PowerShell, not tasklist.
REM
REM  tasklist truncates the IMAGENAME column and its /fi filter compares against
REM  the TRUNCATED value, so "Jalyro.Convert.Host.exe" never matched. This
REM  script cheerfully reported "nothing running" while the Host held every DLL
REM  in stage\ open. That one bug is the cause of every file-locking round in
REM  this project.
REM ===========================================================================

REM  ffmpeg is matched by PATH, not just by name. The two Jalyro names are
REM  distinctive; "ffmpeg" is not, and killing every ffmpeg.exe would end a
REM  user's own encode. Program.cs already scopes its orphan sweep this way.

REM  Paths go through the environment, not interpolated into a quoted
REM  PowerShell literal: a directory named O'Brien closes the string early.
REM
REM  Roots get a trailing separator before comparison. Without it
REM  "...\jalyro-convert" also prefixes "...\jalyro-convert-old" and the
REM  script kills the unrelated encode it promises not to touch.
set "HERE=%~dp0"

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$roots = @();" ^
  "$r = Resolve-Path (Join-Path $env:HERE '..') -ErrorAction SilentlyContinue;" ^
  "if ($r) { $roots += $r.Path };" ^
  "$roots += (Join-Path $env:LOCALAPPDATA 'Programs\JalyroConvert');" ^
  "$roots = @($roots | ForEach-Object { $_.TrimEnd('\') + '\' });" ^
  "$names = 'Jalyro.Convert.Host','Jalyro.Convert.Worker','ffmpeg';" ^
  "$found = @(Get-Process -Name $names -ErrorAction SilentlyContinue | Where-Object {" ^
  "    if ($_.Name -ne 'ffmpeg') { $true } else {" ^
  "      $path = $null; try { $path = $_.Path } catch { };" ^
  "      $match = $false;" ^
  "      if ($path) { foreach ($root in $roots) {" ^
  "        if ($path.StartsWith($root, 'OrdinalIgnoreCase')) { $match = $true } } };" ^
  "      $match } });" ^
  "if (-not $found) { Write-Host '[ok] Nothing of ours is running.'; exit 0 };" ^
  "foreach ($p in $found) {" ^
  "  Write-Host ('Stopping ' + $p.Name + ' (pid ' + $p.Id + ')');" ^
  "  Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue;" ^
  "};" ^
  "Start-Sleep -Milliseconds 1200;" ^
  "$ids = @($found | ForEach-Object { $_.Id });" ^
  "$still = @(Get-Process -Id $ids -ErrorAction SilentlyContinue);" ^
  "if ($still) {" ^
  "  Write-Host '[WARN] Still running:' -ForegroundColor Yellow;" ^
  "  $still | Select-Object Id,Name | Format-Table | Out-String | Write-Host;" ^
  "  exit 1" ^
  "};" ^
  "Write-Host '[ok] Stopped.'"

exit /b %errorlevel%
