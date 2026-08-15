@echo off
setlocal EnableExtensions EnableDelayedExpansion

REM ===========================================================================
REM  sign-installer.cmd <certificate-thumbprint>
REM
REM  Signs the installer produced by make-installer.cmd. Separate from sign.cmd
REM  because the installer does not exist yet when the package is signed - it
REM  packs the already-signed stage.
REM
REM  The setup .exe is the file users actually download, so it is the signature
REM  SmartScreen and the UAC prompt show. Leaving it unsigned wastes every
REM  signature inside it.
REM
REM  NOT signed here: the uninstaller Inno embeds. That needs Inno's own
REM  SignTool= directive, which would put a signing command in setup.iss.
REM  See docs\signing.md.
REM ===========================================================================

cd /d "%~dp0\.."
set "ROOT=%CD%"

if "%~1"=="" (
    echo Usage: sign-installer.cmd ^<certificate-thumbprint^>
    echo.
    echo Find your thumbprint with:  build\get-cert-subject.cmd
    exit /b 1
)
set "THUMB=%~1"

REM RFC-3161 timestamp server. Override before calling to use another, e.g.
REM SSL.com's own:  set "TSURL=http://ts.ssl.com"
REM Without a timestamp the signature dies the day the certificate expires.
if not defined TSURL set "TSURL=http://timestamp.digicert.com"

where signtool.exe >nul 2>&1
if errorlevel 1 (
    echo [FAIL] signtool.exe not found. Install the Windows SDK component.
    exit /b 1
)

REM Find the installer rather than composing its name from a version number
REM held in three places. Exactly one, or stop: a leftover from an older
REM version sitting beside the new one would otherwise be a coin toss.
set "SETUP="
set "COUNT=0"
for %%f in ("%ROOT%\dist\*.exe") do (
    set /a COUNT+=1
    set "SETUP=%%~ff"
)

if "%COUNT%"=="0" (
    echo [FAIL] no .exe in dist\. Run build\make-installer.cmd first.
    exit /b 1
)
if not "%COUNT%"=="1" (
    echo [FAIL] %COUNT% .exe files in dist\ - which one is the installer?
    dir /b "%ROOT%\dist\*.exe"
    echo.
    echo        Delete the ones you are not shipping and run this again.
    exit /b 1
)

echo.
echo === Signing installer =====================================================
echo.
echo   File       : %SETUP%
echo   Thumbprint : %THUMB%
echo   Timestamp  : %TSURL%
echo.

signtool.exe sign /sha1 %THUMB% /fd SHA256 /tr %TSURL% /td SHA256 /v "%SETUP%"
if errorlevel 1 (
    echo.
    echo ===========================================================================
    echo  INSTALLER SIGNING FAILED
    echo ===========================================================================
    exit /b 1
)

echo.
echo --- Verifying ---
signtool.exe verify /pa /v "%SETUP%"
if errorlevel 1 (
    echo.
    echo [FAIL] The signature did not verify against the machine's trust stores.
    echo        A development certificate only verifies where it was trusted by
    echo        hand; a publicly issued one should verify anywhere.
    exit /b 1
)

echo.
echo ===========================================================================
echo  INSTALLER SIGNED
echo.
echo  Publish this file:
echo      %SETUP%
echo ===========================================================================
exit /b 0
