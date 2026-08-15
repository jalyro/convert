<#
.SYNOPSIS
    Downloads ffmpeg into src\ffmpeg, verifying it before installing.

.DESCRIPTION
    A real script rather than a caret-continued cmd one-liner. The previous
    version assembled ~30 lines of PowerShell from continued cmd strings, and a
    "#" comment inside it silently commented out everything that followed on the
    joined line - including the step that installed the download. The script
    reported success and installed nothing.

    LICENSING. ffmpeg is LGPL-2.1+ by default. H.264 output needs libx264, which
    is GPL-2.0+, and enabling it puts the whole build under the GPL. This project
    keeps its own code MIT and invokes ffmpeg.exe as a separate program over its
    documented CLI - the standard aggregation position. Ship the licence text and
    honour the source offer.

    INTEGRITY. The archive is extracted to a temporary location, hashed and
    test-run BEFORE it may replace src\ffmpeg. The swap keeps a backup and rolls
    back on failure; a swap interrupted by process kill or power loss is
    repaired at the start of the next run.

    gpl-shared is deliberately unsupported: this installs only ffmpeg.exe, and
    that build needs a dozen av*.dll files beside it.
#>

[CmdletBinding()]
param(
    [ValidateSet('gpl', 'lgpl')]
    [string]$Variant = 'gpl'
)

$ErrorActionPreference = 'Stop'

$root     = Split-Path -Parent $PSScriptRoot
$dest     = Join-Path $root 'src\ffmpeg'
$incoming = "$dest.new"
$backup   = "$dest.old"
$pinFile  = Join-Path $PSScriptRoot 'ffmpeg-expected.sha256'
$staging  = Join-Path $env:TEMP "jalyro-ffmpeg-$([guid]::NewGuid().ToString('N'))"
$url      = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-$Variant.zip"

function Remove-Quietly([string]$path) {
    if ($path -and (Test-Path $path)) {
        Remove-Item $path -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# A kill or power loss between the two swap moves leaves the working install
# in ffmpeg.old and nothing at ffmpeg. Restore it before any code below may
# delete the backup. Both present = swap completed, only cleanup was missed.
if (-not (Test-Path $dest) -and (Test-Path $backup)) {
    Move-Item $backup $dest
    Write-Host "[ok] restored ffmpeg left in ffmpeg.old by an interrupted swap." -ForegroundColor Green
}
elseif ((Test-Path $dest) -and (Test-Path $backup)) {
    Remove-Quietly $backup
}

Write-Host ""
Write-Host "=== Fetching ffmpeg ($Variant) ===" -ForegroundColor Cyan
Write-Host "  $url"
Write-Host ""

try {
    New-Item -ItemType Directory -Path $staging -Force | Out-Null

    $zip = Join-Path $staging 'ffmpeg.zip'
    $ProgressPreference = 'SilentlyContinue'
    Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing

    $extracted = Join-Path $staging 'x'
    Expand-Archive -Path $zip -DestinationPath $extracted -Force

    $exe = Get-ChildItem $extracted -Recurse -Filter ffmpeg.exe | Select-Object -First 1
    if (-not $exe) { throw "ffmpeg.exe was not found in the archive" }

    $actual = (Get-FileHash $exe.FullName -Algorithm SHA256).Hash

    $pin = Get-Content $pinFile -ErrorAction SilentlyContinue |
           Where-Object { $_ -and -not $_.TrimStart().StartsWith('#') } |
           Select-Object -First 1

    if ($pin) {
        if ($pin.Trim() -ne $actual) {
            Write-Host "[FAIL] hash mismatch - nothing was installed." -ForegroundColor Red
            Write-Host "       expected $($pin.Trim())"
            Write-Host "       actual   $actual"
            exit 1
        }
        Write-Host "[ok] matches the pinned hash." -ForegroundColor Green
    }
    else {
        Write-Host "[WARN] ffmpeg is UNPINNED - development only." -ForegroundColor Yellow
        Write-Host "       A release must pin it. Put this line in" -ForegroundColor Yellow
        Write-Host "       build\ffmpeg-expected.sha256:" -ForegroundColor Yellow
        Write-Host "       $actual" -ForegroundColor Yellow
    }

    # Refuse a build that cannot start. Existence and hash checks alone would
    # let a pinned but unusable exe through. Checked while the current install
    # is still untouched, so failure needs no rollback.
    # Drain the output completely BEFORE selecting a line: piping a native
    # command into Select-Object -First stops it early and can leave
    # $LASTEXITCODE unset (PowerShell issue #19848), and $null -ne 0 would
    # reject a valid exe.
    try {
        $verOutput  = @(& $exe.FullName -version 2>&1)
        $ffmpegExit = $LASTEXITCODE
    }
    catch {
        throw "downloaded ffmpeg.exe could not be started: $($_.Exception.Message)"
    }
    if ($ffmpegExit -ne 0) {
        throw "downloaded ffmpeg.exe exited with code $ffmpegExit"
    }
    $verLine = $verOutput | Select-Object -First 1
    if (-not $verLine) {
        throw "downloaded ffmpeg.exe produced no version output"
    }

    # Build the replacement completely before touching the existing one.
    Remove-Quietly $incoming
    New-Item -ItemType Directory -Path $incoming -Force | Out-Null
    Copy-Item $exe.FullName (Join-Path $incoming 'ffmpeg.exe') -Force

    Get-ChildItem $extracted -Recurse -Include LICENSE*, COPYING* -File |
        ForEach-Object { Copy-Item $_.FullName (Join-Path $incoming $_.Name) -Force }

    $actual | Out-File (Join-Path $incoming 'ffmpeg.sha256') -Encoding ascii

    # Swap with rollback. Delete-then-move would lose a working install if the
    # move failed or the process died between the two.
    Remove-Quietly $backup
    $movedAside = $false
    if (Test-Path $dest) {
        Move-Item $dest $backup
        $movedAside = $true
    }

    try {
        Move-Item $incoming $dest
    }
    catch {
        if ($movedAside) {
            Move-Item $backup $dest
            Write-Host "[FAIL] install failed; the previous ffmpeg was restored." -ForegroundColor Red
        }
        throw
    }

    Remove-Quietly $backup
    Write-Host "[ok] installed, sha256 $actual" -ForegroundColor Green
}
catch {
    Write-Host "[FAIL] $($_.Exception.Message)" -ForegroundColor Red
    Remove-Quietly $incoming
    Remove-Quietly $staging
    exit 1
}
finally {
    Remove-Quietly $staging
}

if (-not (Test-Path (Join-Path $dest 'ffmpeg.exe'))) {
    Write-Host "[FAIL] ffmpeg.exe is not present after install." -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host $verLine   # captured by the pre-swap runnability check

Write-Host ""
Write-Host "  Variant: $Variant"
if ($Variant -eq 'gpl') {
    Write-Host "  Includes libx264 - MP4 output will work."
    Write-Host "  Ship the licence files in $dest when you distribute."
}
else {
    Write-Host "  NO libx264 - MP4 output will NOT work. Re-run with: fetch-ffmpeg.cmd gpl" -ForegroundColor Yellow
}
Write-Host ""
Write-Host "  Next: build\build.cmd"
exit 0
