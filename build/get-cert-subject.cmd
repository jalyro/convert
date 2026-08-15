@echo off
setlocal EnableExtensions

REM ===========================================================================
REM  get-cert-subject.cmd
REM
REM  Prints the exact Subject string of each code-signing certificate in your
REM  personal store, formatted ready to paste into AppxManifest.xml.
REM
REM  The Publisher attribute in the manifest must match the certificate Subject
REM  CHARACTER FOR CHARACTER. Copy the line this prints - do not retype it.
REM ===========================================================================

echo.
echo === Code-signing certificates in CurrentUser\My ===========================
echo.

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$c = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert;" ^
  "if (-not $c) { Write-Host '  (none found)' -ForegroundColor Yellow;" ^
  "               Write-Host '';" ^
  "               Write-Host '  Also check Cert:\LocalMachine\My if your cert is machine-scoped.'; exit }" ^
  "foreach ($x in $c) {" ^
  "  Write-Host ('Thumbprint : ' + $x.Thumbprint);" ^
  "  Write-Host ('Expires    : ' + $x.NotAfter);" ^
  "  Write-Host ('Subject    : ' + $x.Subject);" ^
  "  Write-Host '';" ^
  "  Write-Host 'Paste this into AppxManifest.xml:' -ForegroundColor Green;" ^
  "  Write-Host ('    Publisher=\"' + $x.Subject + '\"') -ForegroundColor Green;" ^
  "  Write-Host ('-' * 74);" ^
  "}"

echo.
endlocal
