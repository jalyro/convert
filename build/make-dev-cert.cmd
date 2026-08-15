@echo off
setlocal EnableExtensions

REM ===========================================================================
REM  make-dev-cert.cmd - create a self-signed development certificate
REM
REM  Windows will NOT install an unsigned MSIX, so a certificate is mandatory
REM  even for development builds. Use this instead of burning signatures on a
REM  paid or metered code-signing certificate.
REM
REM  The subject deliberately carries multiple RDNs (O=, C=) rather than a
REM  bare CN=, so the Publisher-matching rule bites now, while it is free,
REM  rather than later with your real OV certificate. No L= - a locality is
REM  personal data in a public repository, and inventing a town is worse.
REM
REM  Run the FIRST half non-elevated, then the elevated step it prints.
REM ===========================================================================

cd /d "%~dp0\.."
set "ROOT=%CD%"

echo.
echo === Creating self-signed code-signing certificate =========================
echo.

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$c = New-SelfSignedCertificate -Type CodeSigningCert" ^
  "  -Subject 'CN=Jalyro Dev, O=Jalyro Dev, C=CH'" ^
  "  -KeyUsage DigitalSignature -FriendlyName 'Jalyro Dev'" ^
  "  -CertStoreLocation 'Cert:\CurrentUser\My'" ^
  "  -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3','2.5.29.19={text}');" ^
  "Export-Certificate -Cert $c -FilePath (Join-Path $env:ROOT 'dev-cert.cer') | Out-Null;" ^
  "Write-Host ('Thumbprint : ' + $c.Thumbprint);" ^
  "Write-Host ('Subject    : ' + $c.Subject);" ^
  "Write-Host '';" ^
  "Write-Host 'Use the Subject EXACTLY as printed above - PowerShell may have' -ForegroundColor Yellow;" ^
  "Write-Host 'normalised the RDN order from what this script requested.' -ForegroundColor Yellow;" ^
  "Write-Host '';" ^
  "Write-Host 'NEXT, in an ELEVATED PowerShell, run:' -ForegroundColor Green;" ^
  "Write-Host ('  Import-Certificate -FilePath ''' + (Join-Path $env:ROOT 'dev-cert.cer') + ''' -CertStoreLocation Cert:\LocalMachine\TrustedPeople') -ForegroundColor Green"

echo.
endlocal
