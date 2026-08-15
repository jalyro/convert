@echo off
setlocal EnableExtensions EnableDelayedExpansion

REM ===========================================================================
REM  check-prereqs.cmd - verify the toolchain BEFORE wasting a build
REM  Run from: x64 Native Tools Command Prompt for VS 2026
REM ===========================================================================

set "FAIL=0"

echo.
echo === Prerequisites =========================================================
echo.

where cl.exe >nul 2>&1 && (echo [ok  ] MSVC cl.exe) || (echo [MISS] MSVC cl.exe - not in a developer prompt & set FAIL=1)

if /I "%VSCMD_ARG_TGT_ARCH%"=="x64" (echo [ok  ] x64 target) else (echo [MISS] target is "%VSCMD_ARG_TGT_ARCH%", need x64 & set FAIL=1)

where rc.exe        >nul 2>&1 && (echo [ok  ] rc.exe)        || (echo [MISS] rc.exe - Windows SDK component & set FAIL=1)
where makeappx.exe  >nul 2>&1 && (echo [ok  ] makeappx.exe)  || (echo [MISS] makeappx.exe - Windows SDK component & set FAIL=1)
where signtool.exe  >nul 2>&1 && (echo [ok  ] signtool.exe)  || (echo [MISS] signtool.exe - Windows SDK component & set FAIL=1)

echo.
where dotnet.exe >nul 2>&1
if errorlevel 1 (
    echo [MISS] dotnet.exe not found.
    echo        Install the .NET 10 SDK: https://dotnet.microsoft.com/download
    set FAIL=1
) else (
    echo [ok  ] dotnet.exe
    set "HAS10="
    for /f "tokens=1" %%v in ('dotnet --list-sdks 2^>nul') do (
        echo %%v | findstr /b "10." >nul && set "HAS10=1"
    )
    if defined HAS10 (
        echo [ok  ] .NET 10 SDK present
    ) else (
        echo [MISS] No .NET 10 SDK. Installed SDKs:
        dotnet --list-sdks
        set FAIL=1
    )
)

echo.
set "ISCC="
if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe"      set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
where ISCC.exe >nul 2>&1 && for /f "delims=" %%i in ('where ISCC.exe') do set "ISCC=%%i"

if defined ISCC (
    echo [ok  ] Inno Setup: !ISCC!
) else (
    echo [MISS] Inno Setup 6 not found.
    echo        Download: https://jrsoftware.org/isdl.php
    echo        Or:       winget install JRSoftware.InnoSetup
    echo        ^(only needed for make-installer.cmd; build/pack/sign work without it^)
)

echo.
echo ===========================================================================
if "%FAIL%"=="1" (
    echo  MISSING PREREQUISITES - install the items marked [MISS] above.
    exit /b 1
)
echo  All required prerequisites present.
echo ===========================================================================
exit /b 0
