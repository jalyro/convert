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

REM  The package family name is a hash of the Publisher string, so a different
REM  certificate is a different package. Windows would register both, and both
REM  claim the same CLSID and the same verb - two Convert entries in the menu,
REM  one of them pointing at a stage that no longer exists.
set "MANIFEST_PUBLISHER="
for /f "usebackq delims=" %%p in (`powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0set-publisher.ps1" -Show`) do set "MANIFEST_PUBLISHER=%%p"

if not defined MANIFEST_PUBLISHER (
    echo [FAIL] Could not read Publisher from package\AppxManifest.xml.
    echo        Run  build\set-publisher.cmd ^<thumbprint^>  first.
    exit /b 1
)

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$bad = @();" ^
  "foreach ($x in @(Get-AppxPackage -Name 'Jalyro.Convert')) {" ^
  "  if ($x.Publisher -ne $env:MANIFEST_PUBLISHER) { $bad += $x } };" ^
  "if (-not $bad) { exit 0 };" ^
  "Write-Host '[FAIL] a Jalyro.Convert package is registered under a different' -ForegroundColor Red;" ^
  "Write-Host '       publisher, so this build is a separate package:' -ForegroundColor Red;" ^
  "foreach ($x in $bad) {" ^
  "  Write-Host ('         ' + $x.PackageFullName);" ^
  "  Write-Host ('         ' + $x.Publisher) };" ^
  "Write-Host '';" ^
  "Write-Host '       Remove it first, or the menu will carry two entries:';" ^
  "Write-Host '           build\uninstall.cmd';" ^
  "exit 1"

if errorlevel 1 exit /b 1

echo.
echo === Registering sparse package ============================================
echo.
echo   Package  : %STAGE%\Jalyro.Convert.msix
echo   External : %STAGE%
echo.

REM  Paths reach PowerShell through the environment. Interpolating them
REM  into a quoted literal breaks on a directory named O'Brien.
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "try {" ^
  "  Add-AppxPackage -Path (Join-Path $env:STAGE 'Jalyro.Convert.msix') -ExternalLocation $env:STAGE -ErrorAction Stop;" ^
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
