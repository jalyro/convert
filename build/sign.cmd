@echo off
setlocal EnableExtensions EnableDelayedExpansion

REM ===========================================================================
REM  sign.cmd <certificate-thumbprint>
REM
REM  Signing order matters: binaries INSIDE the external location first, then
REM  the package. Signing the package first then touching a binary invalidates
REM  nothing (sparse packages do not hash external content) but keeps the
REM  habit correct for the real product, where content IS inside the package.
REM ===========================================================================

cd /d "%~dp0\.."
set "ROOT=%CD%"
set "STAGE=%ROOT%\stage"

if "%~1"=="" (
    echo Usage: sign.cmd ^<certificate-thumbprint^>
    echo.
    echo Find your thumbprint with:  build\get-cert-subject.cmd
    exit /b 1
)
set "THUMB=%~1"

REM RFC-3161 timestamp server. Any public one works; this is DigiCert's.
REM Without a timestamp your signatures die the day the certificate expires.
REM Override before calling to use another, e.g. SSL.com's own:
REM     set "TSURL=http://ts.ssl.com"
if not defined TSURL set "TSURL=http://timestamp.digicert.com"

where signtool.exe >nul 2>&1
if errorlevel 1 (
    echo [FAIL] signtool.exe not found. Install the Windows SDK component.
    exit /b 1
)

if not exist "%STAGE%\Jalyro.Convert.msix" (
    echo [FAIL] stage\Jalyro.Convert.msix missing. Run make-package.cmd first.
    exit /b 1
)

echo.
echo === Signing with thumbprint %THUMB% =======================================
echo.

REM signtool cannot write to a file that is currently executing.
call "%~dp0stop-host.cmd"
echo.

echo --- 1/4 Jalyro.Convert.Shell.dll ---
signtool.exe sign /sha1 %THUMB% /fd SHA256 /tr %TSURL% /td SHA256 /v "%STAGE%\Jalyro.Convert.Shell.dll"
if errorlevel 1 goto :failed

echo.
echo --- 2/4 Jalyro.Convert.Host.exe ---
signtool.exe sign /sha1 %THUMB% /fd SHA256 /tr %TSURL% /td SHA256 /v "%STAGE%\Jalyro.Convert.Host.exe"
if errorlevel 1 goto :failed

echo.
echo --- 3/4 Jalyro.Convert.Worker.exe ---
signtool.exe sign /sha1 %THUMB% /fd SHA256 /tr %TSURL% /td SHA256 /v "%STAGE%\Jalyro.Convert.Worker.exe"
if errorlevel 1 goto :failed

echo.
echo --- 4/4 Jalyro.Convert.msix ---
signtool.exe sign /sha1 %THUMB% /fd SHA256 /tr %TSURL% /td SHA256 /v "%STAGE%\Jalyro.Convert.msix"
if errorlevel 1 (
    echo.
    echo [FAIL] Package signing failed.
    echo.
    echo   If the error mentions "does not match package manifest publisher",
    echo   the Publisher in AppxManifest.xml is not character-for-character
    echo   identical to the certificate Subject.
    echo.
    echo   Run  build\get-cert-subject.cmd  and copy the printed line exactly.
    echo   Watch for: comma spacing, RDN order, and OID-encoded fields such as
    echo   SERIALNUMBER= that EV/OV certs sometimes carry.
    goto :failed
)

echo.
echo --- Verifying ---
signtool.exe verify /pa /v "%STAGE%\Jalyro.Convert.Shell.dll" || goto :failed
signtool.exe verify /pa /v "%STAGE%\Jalyro.Convert.Host.exe" || goto :failed
signtool.exe verify /pa /v "%STAGE%\Jalyro.Convert.Worker.exe" || goto :failed
signtool.exe verify /pa /v "%STAGE%\Jalyro.Convert.msix" || goto :failed

echo.
echo ===========================================================================
echo  SIGNING OK
echo.
echo  Next: build\install.cmd
echo.
echo  The installer is signed separately, after make-installer.cmd:
echo      build\sign-installer.cmd %THUMB%
echo ===========================================================================
exit /b 0

:failed
echo.
echo ===========================================================================
echo  SIGNING FAILED
echo ===========================================================================
exit /b 1
