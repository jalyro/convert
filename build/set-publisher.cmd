@echo off
setlocal EnableExtensions

REM ===========================================================================
REM  set-publisher.cmd <certificate-thumbprint>
REM  set-publisher.cmd /revert
REM
REM  Thin wrapper around set-publisher.ps1. The logic lives in a real script:
REM  it edits a file, and caret-continued cmd strings are the wrong place for
REM  anything that can report success without having changed anything.
REM
REM  Labels rather than if-blocks, because %errorlevel% inside a parenthesised
REM  block expands when the block is parsed - before the command in it runs.
REM ===========================================================================

if /I "%~1"=="/revert" goto :revert
if "%~1"=="" goto :usage

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0set-publisher.ps1" -Thumbprint %~1
exit /b %errorlevel%

:revert
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0set-publisher.ps1" -Revert
exit /b %errorlevel%

:usage
echo Usage: set-publisher.cmd ^<certificate-thumbprint^>
echo        set-publisher.cmd /revert
echo.
echo Writes your certificate's exact Subject into package\AppxManifest.xml,
echo or puts the committed placeholder back.
echo.
echo List the certificates you have:  build\get-cert-subject.cmd
exit /b 1
