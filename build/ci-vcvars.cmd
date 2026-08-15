@echo off
REM ===========================================================================
REM  ci-vcvars.cmd - put the installed C++ toolset on PATH
REM
REM  Found with vswhere, not a hardcoded year: GitHub moved windows-latest from
REM  Visual Studio 2022 to 2026 in June 2026, both hardcoded paths missed, and
REM  cmd then found Git's GNU link instead of the linker.
REM
REM  Every workflow "run:" block is a separate process, so each step needing
REM  cl, link, rc or dumpbin has to call this again.
REM ===========================================================================

set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" (
    echo [FAIL] vswhere.exe not found at "%VSWHERE%"
    exit /b 1
)

for /f "usebackq delims=" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSDIR=%%i"
if not defined VSDIR (
    echo [FAIL] No Visual Studio install carries the C++ toolset.
    exit /b 1
)

echo Using %VSDIR%
call "%VSDIR%\VC\Auxiliary\Build\vcvars64.bat"
