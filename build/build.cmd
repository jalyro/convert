@echo off
setlocal EnableExtensions EnableDelayedExpansion

REM ===========================================================================
REM  build.cmd - Jalyro Convert
REM
REM  RUN THIS FROM: "x64 Native Tools Command Prompt for VS 2026"
REM
REM  Produces, in .\out\ :
REM      Jalyro.Convert.Shell.dll   the IExplorerCommand shell extension
REM      Jalyro.Convert.Host.exe    resident job listener (.NET 10 / WPF)
REM      Jalyro.Convert.Worker.exe  one conversion per process (libvips)
REM ===========================================================================

cd /d "%~dp0\.."
set "ROOT=%CD%"
set "OUT=%ROOT%\out"

echo.
echo === Jalyro Convert build =============================================
echo.

REM --- Verify we are in the right shell --------------------------------------
where cl.exe >nul 2>&1
if errorlevel 1 (
    echo [FAIL] cl.exe not found on PATH.
    echo.
    echo        Open "x64 Native Tools Command Prompt for VS 2026" and run
    echo        this script from there. Do not use a plain cmd.exe.
    exit /b 1
)

if /I not "%VSCMD_ARG_TGT_ARCH%"=="x64" (
    echo [FAIL] Target architecture is "%VSCMD_ARG_TGT_ARCH%", expected "x64".
    echo.
    echo        You are probably in the x86 developer prompt. Open
    echo        "x64 Native Tools Command Prompt for VS 2026" instead.
    exit /b 1
)

where rc.exe >nul 2>&1
if errorlevel 1 (
    echo [FAIL] rc.exe not found. The Windows SDK component is missing from
    echo        your Visual Studio installation.
    exit /b 1
)

echo [ok] Toolchain: %VSCMD_ARG_HOST_ARCH% host -^> %VSCMD_ARG_TGT_ARCH% target
for /f "tokens=*" %%v in ('cl.exe 2^>^&1 ^| findstr /C:"Version"') do echo [ok] %%v
echo.

if not exist "%OUT%" mkdir "%OUT%"
pushd "%OUT%" >nul

REM ===========================================================================
REM  Compiler flags
REM
REM  /MT  static CRT on purpose. A shell extension that needs the VC++
REM       redistributable is a shell extension that silently fails to load on
REM       a clean machine.
REM  /WX  warnings as errors. This code runs inside a system process.
REM  /GS /guard:cf /Qspectre  hardening, appropriate for code that parses
REM       attacker-influenced filenames.
REM ===========================================================================
set "CFLAGS=/nologo /c /EHsc /W4 /WX /std:c++20 /permissive- /O2 /MT /GS /guard:cf /DUNICODE /D_UNICODE /DWIN32 /D_WINDOWS /DNDEBUG"
set "LFLAGS=/nologo /INCREMENTAL:NO /DYNAMICBASE /NXCOMPAT /guard:cf /OPT:REF /OPT:ICF"
set "LIBS=shlwapi.lib ole32.lib oleaut32.lib shell32.lib user32.lib advapi32.lib kernel32.lib"

REM ---------------------------------------------------------------------------
REM  1. Shell extension DLL
REM ---------------------------------------------------------------------------
echo --- Building Jalyro.Convert.Shell.dll ---

rc.exe /nologo /fo "%OUT%\Shell.res" /I "%ROOT%\src\Shell" "%ROOT%\src\Shell\Shell.rc"
if errorlevel 1 goto :failed

cl.exe %CFLAGS% /I "%ROOT%\src\Shell" ^
    "%ROOT%\src\Shell\ExplorerCommand.cpp" ^
    "%ROOT%\src\Shell\Common.cpp"
if errorlevel 1 goto :failed

link.exe %LFLAGS% /DLL /DEF:"%ROOT%\src\Shell\Shell.def" ^
    /OUT:"%OUT%\Jalyro.Convert.Shell.dll" ^
    ExplorerCommand.obj Common.obj Shell.res %LIBS%
if errorlevel 1 goto :failed

echo [ok] Jalyro.Convert.Shell.dll
echo.

REM ---------------------------------------------------------------------------
REM  2. Host (C# / .NET 10 / WPF, no XAML)
REM ---------------------------------------------------------------------------
echo --- Building Jalyro.Convert.Host.exe ---

where dotnet.exe >nul 2>&1
if errorlevel 1 (
    echo [FAIL] dotnet.exe not found. Run build\check-prereqs.cmd first.
    goto :failed
)

dotnet publish "%ROOT%\src\Host\Jalyro.Convert.Host.csproj" -c Release -o "%OUT%" --nologo
if errorlevel 1 goto :failed

if not exist "%OUT%\Jalyro.Convert.Host.exe" (
    echo [FAIL] dotnet publish succeeded but produced no Host exe.
    goto :failed
)

echo [ok] Jalyro.Convert.Host.exe
echo.

REM ---------------------------------------------------------------------------
REM  3. Worker (C# / .NET 10, libvips via NetVips)
REM
REM  First build pulls NetVips + NetVips.Native.win-x64 from NuGet (~40 MB of
REM  native libvips). Needs internet once; cached afterwards.
REM ---------------------------------------------------------------------------
echo --- Building Jalyro.Convert.Worker.exe ---

dotnet publish "%ROOT%\src\Worker\Jalyro.Convert.Worker.csproj" -c Release -o "%OUT%" --nologo
if errorlevel 1 goto :failed

if not exist "%OUT%\Jalyro.Convert.Worker.exe" (
    echo [FAIL] dotnet publish succeeded but produced no Worker exe.
    goto :failed
)

if not exist "%OUT%\NetVips.dll" (
    echo [FAIL] NetVips.dll missing from the publish output.
    echo        Check that NuGet restore succeeded.
    goto :failed
)

echo [ok] Jalyro.Convert.Worker.exe
echo.

REM ---------------------------------------------------------------------------
REM  4. ffmpeg (optional at build time, required for audio/video and HEIC)
REM ---------------------------------------------------------------------------
if exist "%ROOT%\src\ffmpeg\ffmpeg.exe" (
    if not exist "%OUT%\ffmpeg" mkdir "%OUT%\ffmpeg"
    xcopy /y /q "%ROOT%\src\ffmpeg\*" "%OUT%\ffmpeg\" >nul
    if errorlevel 1 (
        echo [FAIL] Could not copy ffmpeg into out\.
        goto :failed
    )
    echo [ok] ffmpeg staged
) else (
    echo [NOTE] src\ffmpeg\ffmpeg.exe not present.
    echo        Audio, video and HEIC conversions will report the component as
    echo        missing. Run  build\fetch-ffmpeg.cmd  to add it.
)
echo.

REM ---------------------------------------------------------------------------
REM  5. Verify the DLL exports what COM needs
REM ---------------------------------------------------------------------------
echo --- Verifying exports ---
set "FOUND_GCO="
set "FOUND_CUN="
for /f "tokens=*" %%e in ('dumpbin /nologo /exports "%OUT%\Jalyro.Convert.Shell.dll" 2^>nul ^| findstr /C:"DllGetClassObject"') do set "FOUND_GCO=1"
for /f "tokens=*" %%e in ('dumpbin /nologo /exports "%OUT%\Jalyro.Convert.Shell.dll" 2^>nul ^| findstr /C:"DllCanUnloadNow"') do set "FOUND_CUN=1"

if not defined FOUND_GCO (
    echo [FAIL] DllGetClassObject is not exported. Check Shell.def.
    goto :failed
)
if not defined FOUND_CUN (
    echo [FAIL] DllCanUnloadNow is not exported. Check Shell.def.
    goto :failed
)
echo [ok] DllGetClassObject and DllCanUnloadNow are exported
echo.

popd >nul

REM --- Clean up intermediates -------------------------------------------------
del /q "%OUT%\*.obj" "%OUT%\*.res" "%OUT%\*.exp" "%OUT%\*.lib" 2>nul

REM ---------------------------------------------------------------------------
REM  6. Warn if stage is now stale
REM
REM  build.cmd writes to out\. Nothing uses out\ directly - install.cmd and the
REM  installer both run from stage\. Forgetting make-package.cmd means testing
REM  the previous build, which is exactly how v0.2.1 wasted three rounds.
REM ---------------------------------------------------------------------------
if exist "%ROOT%\stage\Jalyro.Convert.Host.exe" (
    echo.
    echo [NOTE] stage\ still holds the PREVIOUS build.
    echo        Nothing you just built is testable until you run:
    echo            build\make-package.cmd
    echo.
)

echo ===========================================================================
echo  BUILD OK
echo.
echo  Output: %OUT%
dir /b "%OUT%"
echo.
echo  Next: build\make-package.cmd
echo ===========================================================================
exit /b 0

:failed
popd >nul 2>&1
echo.
echo ===========================================================================
echo  BUILD FAILED - see the error above
echo ===========================================================================
exit /b 1
