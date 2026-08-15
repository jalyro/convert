@echo off
setlocal EnableExtensions

REM ===========================================================================
REM  fetch-ffmpeg.cmd [gpl|lgpl]
REM
REM  Thin wrapper around fetch-ffmpeg.ps1. The logic lives in a real script
REM  because the previous version assembled ~30 lines of PowerShell from
REM  caret-continued cmd strings, and a "#" inside it commented out everything
REM  after it on the joined line - including the step that installed the
REM  download. It reported success and installed nothing.
REM
REM  gpl-shared is not supported: this installs only ffmpeg.exe, and that build
REM  needs its av*.dll files beside it.
REM ===========================================================================

set "VARIANT=%~1"
if "%VARIANT%"=="" set "VARIANT=gpl"

if /I "%VARIANT%"=="gpl-shared" (
    echo [FAIL] gpl-shared is not supported - it needs its av*.dll files,
    echo        which this script does not install. Use gpl.
    exit /b 1
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0fetch-ffmpeg.ps1" -Variant %VARIANT%
exit /b %errorlevel%
