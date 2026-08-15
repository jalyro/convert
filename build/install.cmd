@echo off
setlocal EnableExtensions

REM ===========================================================================
REM  install.cmd - register the sparse package against the staged layout
REM
REM  DO NOT run this elevated. Add-AppxPackage is a per-user operation; the
REM  real product will install per-user into %LOCALAPPDATA%\Programs for
REM  exactly this reason.
REM ===========================================================================

cd /d "%~dp0\.."
set "ROOT=%CD%"
set "STAGE=%ROOT%\stage"

if not exist "%STAGE%\Jalyro.Convert.msix" (
    echo [FAIL] stage\Jalyro.Convert.msix missing.
    echo        Run make-package.cmd and sign.cmd first.
    exit /b 1
)

echo.
echo === Registering sparse package ============================================
echo.
echo   Package  : %STAGE%\Jalyro.Convert.msix
echo   External : %STAGE%
echo.

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "try {" ^
  "  Add-AppxPackage -Path '%STAGE%\Jalyro.Convert.msix' -ExternalLocation '%STAGE%' -ErrorAction Stop;" ^
  "  Write-Host '[ok] Registered.' -ForegroundColor Green" ^
  "} catch {" ^
  "  Write-Host '[FAIL] ' $_.Exception.Message -ForegroundColor Red;" ^
  "  Write-Host '';" ^
  "  Write-Host 'Common causes:';" ^
  "  Write-Host '  0x800B0109  - the signing cert chain is not trusted on this machine';" ^
  "  Write-Host '  0x80073CF3  - Publisher/Subject mismatch';" ^
  "  Write-Host '  0x80073D02  - a previous version is still in use; restart Explorer';" ^
  "  exit 1" ^
  "}"

if errorlevel 1 exit /b 1

echo.
echo --- Checking context menu mode ---
call "%~dp0check-menu-mode.cmd"

echo.
echo --- Restarting Explorer so the new registration is picked up ---
call "%~dp0restart-explorer.cmd"

echo.
echo ===========================================================================
echo  INSTALLED
echo.
echo  Now right-click a .jpg / .png / .heic file in File Explorer.
echo  You should see "Convert to" in the PRIMARY menu, not under
echo  "Show more options".
echo.
echo  Logs: run  build\show-log.cmd  (it searches both possible paths)
echo ===========================================================================
exit /b 0
