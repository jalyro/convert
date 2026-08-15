@echo off
setlocal EnableExtensions EnableDelayedExpansion

REM ===========================================================================
REM  show-log.cmd - dump the logs
REM
REM  Storage lives at %USERPROFILE%\.jalyro-convert (outside AppData, so MSIX
REM  virtualization cannot redirect it - see docs/decisions.md). Logs go to
REM  logs\shell.log (shell DLL) and logs\host.log (Host process).
REM ===========================================================================

set "LOGS=%USERPROFILE%\.jalyro-convert\logs"

echo.
echo === %LOGS% ==============================================
echo.
if not exist "%LOGS%\shell.log" if not exist "%LOGS%\host.log" (
    echo No logs yet at %LOGS%
    echo Right-click a supported file first.
    exit /b 0
)

echo === shell.log =============================================================
if exist "%LOGS%\shell.log" (type "%LOGS%\shell.log") else (echo   ^(none^))

echo.
echo === host.log ==============================================================
if exist "%LOGS%\host.log" (type "%LOGS%\host.log") else (echo   ^(none^))

if not exist "%LOGS%\shell.log" goto :done

echo.
echo === Host process summary ==================================================
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$f = (Join-Path $env:LOGS 'shell.log');" ^
  "$hosts = Select-String -Path $f -Pattern 'host=(\S+)' | ForEach-Object { $_.Matches[0].Groups[1].Value } | Sort-Object -Unique;" ^
  "foreach ($h in $hosts) {" ^
  "  if ($h -imatch '^dllhost\.exe$') { Write-Host ('  ' + $h + '   <-- CORRECT: isolated from Explorer') -ForegroundColor Green }" ^
  "  elseif ($h -imatch '^explorer\.exe$') { Write-Host ('  ' + $h + '   <-- WRONG: in-process, a crash takes Explorer down') -ForegroundColor Red }" ^
  "  else { Write-Host ('  ' + $h) }" ^
  "}"

echo.
echo === Invoke timings (F2) ===================================================
findstr /C:"elapsed=" "%LOGS%\shell.log"
if errorlevel 1 echo   (no Invoke calls logged yet - click a submenu entry)

:done
echo.
endlocal
