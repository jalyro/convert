@echo off
setlocal EnableExtensions

REM ===========================================================================
REM  uninstall.cmd - remove the sparse package registration
REM
REM  Also the update procedure: you MUST unregister before replacing the
REM  binaries, because the DLL is loaded by the COM surrogate and the files
REM  will be locked otherwise. VS Code hit exactly this in their Inno updater.
REM ===========================================================================

echo.
echo === Removing sparse package registration ==================================
echo.

REM  Stop the Host first. unlock.cmd does; without this, Remove-AppxPackage
REM  has taken ~30 seconds with the Host still running.
call "%~dp0stop-host.cmd"
if errorlevel 1 (
    echo [FAIL] Something of ours is still running. Removing the package now
    echo        would leave files locked. Close any converting window, or
    echo        reboot, then run this again.
    exit /b 1
)

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$p = Get-AppxPackage -Name 'Jalyro.Convert';" ^
  "if (-not $p) { Write-Host '[ok] Not registered - nothing to do.'; exit 0 };" ^
  "try {" ^
  "  foreach ($x in $p) {" ^
  "    Write-Host ('Removing ' + $x.PackageFullName);" ^
  "    Remove-AppxPackage -Package $x.PackageFullName -ErrorAction Stop" ^
  "  }" ^
  "} catch {" ^
  "  Write-Host ('[FAIL] ' + $_.Exception.Message) -ForegroundColor Red;" ^
  "  exit 1" ^
  "};" ^
  "Write-Host '[ok] Removed.' -ForegroundColor Green"

REM  Without this the script printed UNINSTALLED after a failed removal.
if errorlevel 1 exit /b 1

echo.
echo --- Restarting Explorer ---
call "%~dp0restart-explorer.cmd"

echo.
echo ===========================================================================
echo  UNINSTALLED
echo.
echo  Verify nothing is left behind:
echo      powershell -c "Get-AppxPackage -Name Jalyro.Convert"
echo  should print nothing.
echo ===========================================================================
exit /b 0
