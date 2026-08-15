@echo off
setlocal EnableExtensions EnableDelayedExpansion

REM ===========================================================================
REM  make-package.cmd - stage the install layout and build the sparse .msix
REM
REM  RUN THIS FROM: "x64 Native Tools Command Prompt for VS 2026"
REM  RUN build.cmd FIRST.
REM ===========================================================================

cd /d "%~dp0\.."
set "ROOT=%CD%"
set "OUT=%ROOT%\out"
set "STAGE=%ROOT%\stage"
set "PKGSRC=%ROOT%\pkgsrc"

echo.
echo === Packaging =============================================================
echo.

if not exist "%OUT%\Jalyro.Convert.Shell.dll" (
    echo [FAIL] out\Jalyro.Convert.Shell.dll missing. Run build\build.cmd first.
    exit /b 1
)

where makeappx.exe >nul 2>&1
if errorlevel 1 (
    echo [FAIL] makeappx.exe not found on PATH.
    echo        Install the "Windows 10/11 SDK" component in the Visual Studio
    echo        Installer, then reopen the developer prompt.
    exit /b 1
)

REM --- Guard against the single most common mistake ---------------------------
findstr /C:"REPLACE_ME_WITH_EXACT_CERT_SUBJECT" "%ROOT%\package\AppxManifest.xml" >nul
if not errorlevel 1 (
    echo [FAIL] AppxManifest.xml Publisher is still the placeholder.
    echo.
    echo        build\get-cert-subject.cmd        lists your certificates
    echo        build\set-publisher.cmd ^<thumb^>   writes the subject in
    exit /b 1
)

REM  The shipped manifest fills this in, so this guard does not fire here.
REM  Kept for a fork that blanks the field: failing with a sentence beats
REM  failing inside MakeAppx.
findstr /C:"<PublisherDisplayName>REPLACE_ME<" "%ROOT%\package\AppxManifest.xml" >nul
if not errorlevel 1 (
    echo [FAIL] AppxManifest.xml PublisherDisplayName is still the placeholder.
    echo        Any human-readable string will do.
    exit /b 1
)

REM ---------------------------------------------------------------------------
REM  1. Stage: this is what the external location will contain.
REM     In the real product this is what the Inno installer would lay down.
REM ---------------------------------------------------------------------------
REM Unlock BEFORE touching the directory. v0.6.1 ran rmdir first and produced
REM a wall of "Access is denied" before stop-host had even been called.
call "%~dp0unlock.cmd"

if exist "%STAGE%" rmdir /s /q "%STAGE%"
mkdir "%STAGE%"
mkdir "%STAGE%\Assets"

copy /y "%OUT%\Jalyro.Convert.Shell.dll" "%STAGE%\" >nul
if errorlevel 1 (
    echo [FAIL] Could not copy Jalyro.Convert.Shell.dll to stage.
    echo        Unregister the package first: build\uninstall.cmd
    exit /b 1
)

xcopy /y /q "%OUT%\*.exe" "%STAGE%\" >nul
if errorlevel 1 (
    echo [FAIL] Could not copy executables to stage - something has them open.
    exit /b 1
)

xcopy /y /q "%OUT%\*.dll" "%STAGE%\" >nul
if errorlevel 1 (
    echo [FAIL] Could not copy managed assemblies to stage.
    exit /b 1
)

REM runtimeconfig.json is NOT optional. Without it the app host cannot start:
REM   "A fatal error was encountered. The library 'hostpolicy.dll' required to
REM    execute the application was not found"
REM v0.5.1 swallowed this copy's errors with 2>nul and shipped a stage that
REM could not run.
xcopy /y /q "%OUT%\*.json" "%STAGE%\" >nul
if errorlevel 1 (
    echo [FAIL] Could not stage the .json runtime configuration files.
    exit /b 1
)

if not exist "%STAGE%\Jalyro.Convert.Host.runtimeconfig.json" (
    echo [FAIL] Jalyro.Convert.Host.runtimeconfig.json missing from stage.
    echo        The Host will not start without it.
    exit /b 1
)

if exist "%OUT%\ffmpeg" (
    xcopy /y /q /e /i "%OUT%\ffmpeg" "%STAGE%\ffmpeg" >nul
    if errorlevel 1 (
        echo [FAIL] Could not stage ffmpeg.
        exit /b 1
    )
)

REM libvips native binaries live under runtimes\win-x64\native
if exist "%OUT%\runtimes" (
    xcopy /y /q /e /i "%OUT%\runtimes" "%STAGE%\runtimes" >nul
    if errorlevel 1 (
        echo [FAIL] Could not stage runtimes\ ^(libvips native binaries^).
        exit /b 1
    )
)

copy /y "%ROOT%\package\Assets\*.png" "%STAGE%\Assets\" >nul
if errorlevel 1 (
    echo [FAIL] Could not copy assets to stage.
    exit /b 1
)

REM Prove the copy actually happened rather than trusting errorlevel alone.
for %%f in ("%OUT%\Jalyro.Convert.Host.exe") do set "OUTSTAMP=%%~tf"
for %%f in ("%STAGE%\Jalyro.Convert.Host.exe") do set "STAGESTAMP=%%~tf"
echo [ok] Staged install layout: %STAGE%
echo      out   Host.exe: %OUTSTAMP%
echo      stage Host.exe: %STAGESTAMP%

REM ---------------------------------------------------------------------------
REM  2. Package source: manifest + visual assets ONLY.
REM     The DLL and EXE are deliberately NOT packed - they are resolved from
REM     the external location at registration time. That is what makes this a
REM     sparse package rather than a full MSIX.
REM ---------------------------------------------------------------------------
if exist "%PKGSRC%" rmdir /s /q "%PKGSRC%"
mkdir "%PKGSRC%"
mkdir "%PKGSRC%\Assets"

copy /y "%ROOT%\package\AppxManifest.xml" "%PKGSRC%\" >nul
copy /y "%ROOT%\package\Assets\*.png"     "%PKGSRC%\Assets\" >nul
echo [ok] Staged package source: %PKGSRC%

REM ---------------------------------------------------------------------------
REM  3. Build the .msix
REM ---------------------------------------------------------------------------
if exist "%STAGE%\Jalyro.Convert.msix" del /q "%STAGE%\Jalyro.Convert.msix"

makeappx.exe pack /o /d "%PKGSRC%" /p "%STAGE%\Jalyro.Convert.msix" /nv
if errorlevel 1 (
    echo.
    echo [FAIL] makeappx pack failed. The manifest is almost certainly the cause.
    echo        Common issues:
    echo          - a namespace declared but not listed in IgnorableNamespaces
    echo          - MinVersion below 10.0.19041.0 with uap10:AllowExternalContent
    echo          - an asset referenced in the manifest but missing from Assets\
    exit /b 1
)

echo.
echo [ok] %STAGE%\Jalyro.Convert.msix
echo.
echo ===========================================================================
echo  PACKAGE OK
echo.
if not exist "%STAGE%\ffmpeg\ffmpeg.exe" (
    echo.
    echo [FAIL] stage\ffmpeg\ffmpeg.exe missing.
    echo        Audio, video and HEIC are advertised features - a package
    echo        without ffmpeg fails all three. Run build\fetch-ffmpeg.cmd.
    exit /b 1
)

REM Verify independently. Checking only that ffmpeg EXISTS meant a fetch that
REM had already failed its hash check could still be packaged by anyone who
REM missed the earlier error.
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$pin = Get-Content (Join-Path $env:ROOT 'build\ffmpeg-expected.sha256') -ErrorAction SilentlyContinue |" ^
  "       Where-Object { $_ -and -not $_.TrimStart().StartsWith('#') } | Select-Object -First 1;" ^
  "$actual = (Get-FileHash (Join-Path $env:STAGE 'ffmpeg\ffmpeg.exe') -Algorithm SHA256).Hash;" ^
  "if (-not $pin) {" ^
  "  Write-Host '[WARN] ffmpeg is UNPINNED. Fine for development; make-installer will refuse.' -ForegroundColor Yellow;" ^
  "  exit 0 };" ^
  "if ($pin.Trim() -ne $actual) {" ^
  "  Write-Host '[FAIL] staged ffmpeg does not match the pinned hash.' -ForegroundColor Red;" ^
  "  Write-Host ('       expected ' + $pin.Trim());" ^
  "  Write-Host ('       actual   ' + $actual);" ^
  "  exit 1 };" ^
  "Write-Host '[ok] staged ffmpeg matches the pinned hash.'"
if errorlevel 1 exit /b 1

echo  Next: build\sign.cmd  ^<thumbprint^>
echo.
echo  THEN build\install.cmd - required every time now, because unlock.cmd
echo  unregisters the package to release the shell DLL. Without it the
echo  "Convert to" menu will not appear.
echo ===========================================================================
exit /b 0
