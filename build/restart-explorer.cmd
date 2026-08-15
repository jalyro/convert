@echo off
setlocal EnableExtensions

REM ===========================================================================
REM  restart-explorer.cmd
REM
REM  Sequential, with a pause. The v0.1.0 scripts ran taskkill and start on one
REM  line, which races: start can fire while the old shell is still tearing
REM  down, and the taskbar never comes back.
REM
REM  If it still does not return, open Task Manager (Ctrl+Shift+Esc),
REM  Run new task, type explorer.exe, and leave "administrative
REM  privileges" UNCHECKED.
REM ===========================================================================

echo Stopping Explorer...
taskkill /f /im explorer.exe >nul 2>&1

echo Waiting for shutdown to settle...
REM  ping, not timeout: timeout reads the console input buffer and
REM  silently eats commands pasted after this script.
ping -n 4 127.0.0.1 >nul

echo Starting Explorer...
start "" explorer.exe

ping -n 3 127.0.0.1 >nul
tasklist /fi "IMAGENAME eq explorer.exe" | find /i "explorer.exe" >nul
if errorlevel 1 (
    echo.
    echo [WARN] Explorer does not appear to be running.
    echo        Ctrl+Shift+Esc -^> Run new task -^> explorer.exe ^(not elevated^)
    exit /b 1
)
echo [ok] Explorer is running.
exit /b 0
