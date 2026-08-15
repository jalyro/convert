@echo off
setlocal EnableExtensions EnableDelayedExpansion

REM ===========================================================================
REM  make-installer.cmd - compile the Inno Setup installer
REM  Run AFTER build.cmd, make-package.cmd and sign.cmd.
REM ===========================================================================

cd /d "%~dp0\.."
set "ROOT=%CD%"

set "ISCC="
if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe"      set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"

if not defined ISCC (
    echo [FAIL] Inno Setup 6 not found.
    echo        winget install JRSoftware.InnoSetup
    exit /b 1
)

if not exist "%ROOT%\stage\Jalyro.Convert.msix" (
    echo [FAIL] stage\ is not populated. Run make-package.cmd first.
    exit /b 1
)

REM Shipping without ffmpeg produces an installer whose audio, video and HEIC
REM conversions all fail. skipifsourcedoesntexist in setup.iss would let that
REM through silently.
if not exist "%ROOT%\stage\ffmpeg\ffmpeg.exe" (
    echo [FAIL] stage\ffmpeg\ffmpeg.exe missing.
    echo        The installer would ship without audio, video or HEIC support.
    echo        Run  build\fetch-ffmpeg.cmd  then rebuild.
    exit /b 1
)

REM A release must be reproducible. Without a pin, the GPL source offer cannot
REM name the exact binary shipped, and an upstream replacement would go
REM unnoticed. Development builds may be unpinned; installers may not.
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$pin = Get-Content (Join-Path $env:ROOT 'build\ffmpeg-expected.sha256') -ErrorAction SilentlyContinue |" ^
  "       Where-Object { $_ -and -not $_.TrimStart().StartsWith('#') } | Select-Object -First 1;" ^
  "if (-not $pin) {" ^
  "  Write-Host '[FAIL] ffmpeg is not pinned.' -ForegroundColor Red;" ^
  "  Write-Host '       Record the SHA-256 in build\ffmpeg-expected.sha256 before';" ^
  "  Write-Host '       building an installer. Current staged binary:';" ^
  "  Write-Host ('       ' + (Get-FileHash (Join-Path $env:ROOT 'stage\ffmpeg\ffmpeg.exe') -Algorithm SHA256).Hash);" ^
  "  exit 1 };" ^
  "$actual = (Get-FileHash (Join-Path $env:ROOT 'stage\ffmpeg\ffmpeg.exe') -Algorithm SHA256).Hash;" ^
  "if ($pin.Trim() -ne $actual) { Write-Host '[FAIL] staged ffmpeg does not match the pin.' -ForegroundColor Red; exit 1 };" ^
  "Write-Host '[ok] ffmpeg pinned and verified.'"
if errorlevel 1 exit /b 1

if not exist "%ROOT%\dist" mkdir "%ROOT%\dist"

echo.
echo === Compiling installer ===================================================
echo.

"%ISCC%" /Q "%ROOT%\installer\setup.iss"
if errorlevel 1 (
    echo [FAIL] ISCC returned an error.
    exit /b 1
)

echo.
echo [ok] Installer built:
dir /b "%ROOT%\dist\*.exe"
echo.
echo  Sign it before distributing:
echo      signtool sign /sha1 ^<thumbprint^> /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 "%ROOT%\dist\JalyroConvert-Setup-0.9.34.exe"
exit /b 0
