#requires -Version 7

<#
.SYNOPSIS
    Feeds malformed and hostile file CONTENT through the converter.

.DESCRIPTION
    Filenames are one attack surface; the bytes inside are the other, and the
    more dangerous one. libheif, libaom and libavcodec all have CVE histories,
    and this is the code path that parses whatever a user downloaded.

    A PASS is: a clean error, no crash, no hang, no leftover temp files.

    NOTE ON MEMORY: this script runs the Worker DIRECTLY, so it is NOT inside
    the Host's Job Object and no memory ceiling applies. Claiming otherwise
    would be false. To exercise the ceiling, drive a conversion through the
    context menu with a Host running and watch the Worker's peak working set.
#>

param(
    [string]$WorkerPath = (Join-Path $PSScriptRoot "..\..\stage\Jalyro.Convert.Worker.exe"),
    [string]$SourceImage = "$env:USERPROFILE\Documents\cloud.png"
)

if (-not (Test-Path $WorkerPath))  { Write-Error "Worker not found: $WorkerPath"; exit 1 }
if (-not (Test-Path $SourceImage)) { Write-Error "Need a source image: $SourceImage"; exit 1 }

$work = Join-Path $env:TEMP "jalyro-convert-fuzz-content-$(New-Guid)"
New-Item -ItemType Directory -Path $work | Out-Null
Write-Host "Working in $work`n"

$original = [IO.File]::ReadAllBytes($SourceImage)
$rng = [Random]::new(20260810)   # fixed seed: failures must be reproducible

function New-Case {
    param([string]$Name, [byte[]]$Bytes)
    $path = Join-Path $work $Name
    [IO.File]::WriteAllBytes($path, $Bytes)
    return $path
}

$cases = @()

# Truncation - the failure mode that produced three rounds of misdiagnosis on
# the HEIC files.
foreach ($fraction in 0.1, 0.5, 0.9, 0.99) {
    $len = [int]($original.Length * $fraction)
    $cases += @{ name = "truncated-$fraction.png"; bytes = $original[0..($len - 1)] }
}

# Header intact, body corrupted.
$corrupt = $original.Clone()
for ($i = 0; $i -lt 200; $i++) {
    $pos = $rng.Next(64, $corrupt.Length)
    $corrupt[$pos] = [byte]$rng.Next(0, 256)
}
$cases += @{ name = 'bitflips.png'; bytes = $corrupt }

# Zero length.
$cases += @{ name = 'empty.png'; bytes = [byte[]]@() }

# Wrong magic entirely - a text file wearing a PNG extension.
$cases += @{ name = 'not-an-image.png'; bytes = [Text.Encoding]::ASCII.GetBytes('<!DOCTYPE html><html>nope</html>') }

# Header only.
$cases += @{ name = 'header-only.png'; bytes = $original[0..63] }

# Random noise.
$noise = [byte[]]::new(65536)
$rng.NextBytes($noise)
$cases += @{ name = 'noise.png'; bytes = $noise }

$startedAt = Get-Date
$results = @()

foreach ($case in $cases) {
    $src = New-Case -Name $case.name -Bytes $case.bytes
    $dst = Join-Path $work ("out-" + [guid]::NewGuid().ToString('N') + ".jpg")

    # A real timeout. The call operator blocks until the Worker exits, so
    # measuring elapsed time afterwards could only ever report a hang that had
    # already finished - and in CI it would hang the whole job.
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $psi = [Diagnostics.ProcessStartInfo]::new($WorkerPath)
    foreach ($a in @('--input', $src, '--output', $dst, '--format', 'jpg')) {
        $psi.ArgumentList.Add($a)
    }
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true

    $proc = [Diagnostics.Process]::Start($psi)
    $outTask = $proc.StandardOutput.ReadToEndAsync()
    $errTask = $proc.StandardError.ReadToEndAsync()

    $hung = $false
    if (-not $proc.WaitForExit(60000)) {
        try { $proc.Kill($true) } catch { }
        $proc.WaitForExit(5000) | Out-Null
        $hung = $true
    }

    $sw.Stop()
    $output = if ($hung) { '' } else { $outTask.Result + $errTask.Result }
    $code = if ($hung) { -1 } else { $proc.ExitCode }

    $verdict = 'PASS'
    $detail = "exit $code, $($sw.ElapsedMilliseconds) ms"

    if ($hung) {
        # Do NOT 'continue' here. Skipping to the next case meant a hung run
        # never had its temp files checked - so the case most likely to strand
        # files was the one case that never looked.
        $verdict = 'FAIL'; $detail = 'HUNG - killed after 60s'
    }
    elseif ($code -eq 0 -and -not (Test-Path -LiteralPath $dst)) {
        $verdict = 'FAIL'; $detail += ' - reported success but produced nothing'
    }

    # A leftover temp file means a failure path did not clean up. Both
    # locations: output-adjacent .jalyro-convert-* and %TEMP% jalyro-convert-*.
    $leftovers = @(Get-ChildItem $work -Filter '.jalyro-convert-*' -Force -ErrorAction SilentlyContinue)
    $leftovers += @(Get-ChildItem $env:TEMP -Filter 'jalyro-convert-*' -Force -ErrorAction SilentlyContinue |
                    Where-Object { $_.CreationTime -gt $startedAt })
    if ($leftovers) {
        $verdict = 'FAIL'; $detail += " - left $($leftovers.Count) temp file(s) behind"
        $leftovers | Remove-Item -Force -ErrorAction SilentlyContinue
    }

    $results += [pscustomobject]@{ Case = $case.name; Result = $verdict; Detail = $detail }
}

Write-Host ""
$results | Format-Table -AutoSize

$failed = ($results | Where-Object Result -eq 'FAIL').Count
if ($failed -gt 0) {
    Write-Host "$failed FAILED" -ForegroundColor Red
    exit 1          # fail the build, do not merely report
}
Write-Host "All cases passed." -ForegroundColor Green

Write-Host "`nRemove-Item '$work' -Recurse -Force"
