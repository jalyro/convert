@echo off
setlocal EnableExtensions

REM ===========================================================================
REM  get-cert-subject.cmd
REM
REM  Lists the code-signing certificates in your personal store, and prints the
REM  set-publisher command for each.
REM
REM  The Publisher attribute in the manifest must match the certificate Subject
REM  CHARACTER FOR CHARACTER, so nothing here asks you to retype it: pass the
REM  thumbprint to set-publisher.cmd and it copies the subject itself.
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
  "  Write-Host 'Point the manifest at it with:' -ForegroundColor Green;" ^
  "  Write-Host ('    build\set-publisher.cmd ' + $x.Thumbprint) -ForegroundColor Green;" ^
  "  Write-Host ('-' * 74);" ^
  "}"

echo.
endlocal
