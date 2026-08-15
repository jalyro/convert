@echo off
setlocal EnableExtensions

REM ===========================================================================
REM  check-menu-mode.cmd
REM
REM  Detects the "restore the Windows 10 context menu" override.
REM
REM  Phase 0 finding #1: while this key exists, the Windows 11 primary menu is
REM  disabled for the current user and NO IExplorerCommand handler can appear -
REM  ours included. Costs an hour to diagnose if you do not know to look.
REM ===========================================================================

set "KEY=HKCU\Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}"

echo.
reg query "%KEY%" >nul 2>&1
if errorlevel 1 (
    echo [ok] Windows 11 context menu is active.
    echo      IExplorerCommand handlers can appear in the primary menu.
    exit /b 0
)

echo [WARN] The classic ^(Windows 10^) context menu override is SET.
echo.
echo        While this key exists no IExplorerCommand handler will ever be
echo        visible, including Jalyro Convert. The menu will look long and
echo        flat, led by a bold "Open".
echo.
echo        Key: %KEY%
echo.
choice /c YN /m "Remove it now and restart Explorer"
if errorlevel 2 (
    echo Left in place. Tests D1, D2, D4 and D5 cannot pass in this state.
    exit /b 1
)

reg delete "%KEY%" /f
if errorlevel 1 (
    echo [FAIL] Delete failed. A shell tweaking tool ^(ExplorerPatcher,
    echo        StartAllBack, Winaero, Windhawk, Nilesoft Shell^) may own this
    echo        setting and rewrite it. Disable it inside that tool instead.
    exit /b 1
)

echo [ok] Removed. Restarting Explorer so the change takes effect...
call "%~dp0restart-explorer.cmd"
exit /b 0
