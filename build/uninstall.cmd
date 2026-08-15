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

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$p = Get-AppxPackage -Name 'Jalyro.Convert';" ^
  "if (-not $p) { Write-Host '[ok] Not registered - nothing to do.'; exit }" ^
  "foreach ($x in $p) {" ^
  "  Write-Host ('Removing ' + $x.PackageFullName);" ^
  "  Remove-AppxPackage -Package $x.PackageFullName" ^
  "}" ^
  "Write-Host '[ok] Removed.' -ForegroundColor Green"

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
