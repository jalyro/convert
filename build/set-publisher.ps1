<#
.SYNOPSIS
    Points AppxManifest.xml Identity/@Publisher at a certificate in your store,
    or puts the placeholder back before a commit.

.DESCRIPTION
    Identity/@Publisher must equal the signing certificate's Subject character
    for character, or signtool refuses to sign the package. The subject is read
    from the certificate store by thumbprint and never typed into a file: it
    carries the holder's legal name and address, and this repository is public.

    This replaces two hand-pasted one-liners. Both matched one certificate
    subject as a literal string, so both stopped working the moment a different
    certificate was used - the revert silently found nothing to revert.

    -Show   prints the current value and nothing else, for scripts to consume.
    -Revert restores the placeholder whatever the current value is.
#>
[CmdletBinding(DefaultParameterSetName = 'Set')]
param(
    [Parameter(ParameterSetName = 'Set', Mandatory = $true, Position = 0)]
    [string]$Thumbprint,

    [Parameter(ParameterSetName = 'Revert', Mandatory = $true)]
    [switch]$Revert,

    [Parameter(ParameterSetName = 'Show', Mandatory = $true)]
    [switch]$Show
)

$ErrorActionPreference = 'Stop'

$placeholder    = 'CN=REPLACE_ME_WITH_EXACT_CERT_SUBJECT'
$codeSigningOid = '1.3.6.1.5.5.7.3.3'
$manifest       = Join-Path (Split-Path -Parent $PSScriptRoot) 'package\AppxManifest.xml'
$quote          = [string][char]34   # a [char] here binds Replace(char,char)

function Get-ManifestText {
    if (-not (Test-Path $manifest)) { throw "not found: $manifest" }
    return [IO.File]::ReadAllText($manifest)
}

function Get-PublisherSpan([string]$text) {
    # Exact-match anchor: abort unless there is exactly one Publisher attribute.
    # PublisherDisplayName is an element, not an attribute, so it cannot match.
    $needle = 'Publisher=' + $quote
    $first  = $text.IndexOf($needle)
    if ($first -lt 0) { throw "no Publisher attribute in $manifest" }
    if ($text.IndexOf($needle, $first + 1) -ge 0) {
        throw "more than one Publisher attribute in $manifest - refusing to guess"
    }
    $start = $first + $needle.Length
    $end   = $text.IndexOf($quote, $start)
    if ($end -lt 0) { throw 'the Publisher attribute value is never closed' }
    return @{ Start = $start; Length = $end - $start }
}

# A distinguished name may legitimately contain & or " - RFC 4514 escapes them
# for the DN, not for XML. Writing one raw would produce an unparseable manifest.
function ConvertTo-XmlAttr([string]$s) {
    $s = $s.Replace('&', '&amp;')
    $s = $s.Replace('<', '&lt;')
    $s = $s.Replace('>', '&gt;')
    return $s.Replace($quote, '&quot;')
}

function ConvertFrom-XmlAttr([string]$s) {
    $s = $s.Replace('&lt;', '<')
    $s = $s.Replace('&gt;', '>')
    $s = $s.Replace('&quot;', $quote)
    return $s.Replace('&amp;', '&')   # last, or &amp;lt; would decode twice
}

function Set-PublisherValue([string]$newValue) {
    $before = Get-ManifestText
    $span   = Get-PublisherSpan $before
    $old    = ConvertFrom-XmlAttr $before.Substring($span.Start, $span.Length)
    $after  = $before.Remove($span.Start, $span.Length).Insert($span.Start, (ConvertTo-XmlAttr $newValue))

    # UTF-8 without BOM. Nothing outside the attribute is touched, so CRLF
    # survives - but verify it rather than assume: a str_replace on another
    # file introduced bare LFs that nobody thought to check.
    [IO.File]::WriteAllText($manifest, $after, (New-Object Text.UTF8Encoding $false))

    $bytes = [IO.File]::ReadAllBytes($manifest)
    for ($i = 0; $i -lt $bytes.Length; $i++) {
        if ($bytes[$i] -eq 10 -and ($i -eq 0 -or $bytes[$i - 1] -ne 13)) {
            throw "wrote a bare LF at byte $i - the manifest must stay CRLF"
        }
    }

    $reread   = Get-ManifestText
    $check    = Get-PublisherSpan $reread
    $readBack = ConvertFrom-XmlAttr $reread.Substring($check.Start, $check.Length)
    if ($readBack -ne $newValue) {
        throw "read-back mismatch: wrote '$newValue', file now holds '$readBack'"
    }
    return $old
}

if ($Show) {
    $text = Get-ManifestText
    $span = Get-PublisherSpan $text
    Write-Output (ConvertFrom-XmlAttr $text.Substring($span.Start, $span.Length))
    exit 0
}

if ($Revert) {
    $old = Set-PublisherValue $placeholder
    Write-Host ''
    if ($old -eq $placeholder) {
        Write-Host '[ok] already the placeholder - nothing to revert.' -ForegroundColor Green
    }
    else {
        Write-Host '[ok] Publisher reverted to the placeholder.' -ForegroundColor Green
        Write-Host "     was: $old"
    }
    Write-Host ''
    exit 0
}

# --- Set -------------------------------------------------------------------

$wanted = ($Thumbprint -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
if ($wanted.Length -ne 40) {
    throw "'$Thumbprint' is not a thumbprint. A thumbprint is 40 hex characters (SHA-1); a 64-character value is a SHA-256 of something else."
}

$found = @()
foreach ($store in 'Cert:\CurrentUser\My', 'Cert:\LocalMachine\My') {
    if (-not (Test-Path $store)) { continue }
    foreach ($c in @(Get-ChildItem $store -ErrorAction SilentlyContinue)) {
        if ($c.Thumbprint -eq $wanted) { $found += $c }
    }
}
if ($found.Count -eq 0) {
    throw "no certificate with thumbprint $wanted in CurrentUser\My or LocalMachine\My. Run build\get-cert-subject.cmd to list what is there. A cloud certificate must be loaded into the store first."
}

$cert = $found[0]

if (-not $cert.HasPrivateKey) {
    throw "$wanted has no private key - it cannot sign."
}
if ($cert.NotAfter -lt (Get-Date)) {
    throw "$wanted expired on $($cert.NotAfter). Signatures made with it will not validate."
}

$ekus = @()
foreach ($e in $cert.Extensions) {
    if ($e -is [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]) {
        foreach ($o in $e.EnhancedKeyUsages) { $ekus += $o.Value }
    }
}
if ($ekus.Count -gt 0 -and $ekus -notcontains $codeSigningOid) {
    throw "$wanted has no Code Signing EKU ($codeSigningOid). Document signing and S/MIME certificates cannot sign an MSIX."
}
if ($ekus.Count -eq 0) {
    Write-Host '[WARN] this certificate lists no EKU, so it is valid for all purposes.' -ForegroundColor Yellow
}

$daysLeft = [int]($cert.NotAfter - (Get-Date)).TotalDays
if ($daysLeft -lt 30) {
    Write-Host "[WARN] this certificate expires in $daysLeft days." -ForegroundColor Yellow
}

$old = Set-PublisherValue $cert.Subject

Write-Host ''
Write-Host '=== Publisher set ==========================================================' -ForegroundColor Cyan
Write-Host "  was        : $old"
Write-Host "  now        : $($cert.Subject)"
Write-Host "  thumbprint : $($cert.Thumbprint)"
Write-Host "  expires    : $($cert.NotAfter)"
Write-Host ''

if ($old -ne $cert.Subject -and $old -ne $placeholder) {
    Write-Host '  The package family name is a hash of this string, so this is now a' -ForegroundColor Yellow
    Write-Host '  DIFFERENT package. Remove the one registered under the old publisher' -ForegroundColor Yellow
    Write-Host '  before installing, or the menu will carry two entries:' -ForegroundColor Yellow
    Write-Host '      build\uninstall.cmd' -ForegroundColor Yellow
    Write-Host ''
}

Write-Host '  Before committing:  build\set-publisher.cmd /revert'
Write-Host ''
exit 0
