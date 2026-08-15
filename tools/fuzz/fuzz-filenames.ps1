#requires -Version 7

<#
.SYNOPSIS
    Feeds pathological filenames through the converter and reports what happened.

.DESCRIPTION
    The shell extension and the Worker both run against attacker-chosen
    filenames: the user downloaded a file and right-clicked it. The threat model
    in docs/sandboxing.md lists what that enables; this exercises it.

    Nothing here should crash, hang, produce a file outside the working
    directory, or overwrite anything. A refusal with a clear message is a PASS.

.NOTES
    Run from an ordinary prompt. It creates and deletes files in a temp folder
    and touches nothing else.
#>

param(
    [string]$WorkerPath = (Join-Path $PSScriptRoot "..\..\stage\Jalyro.Convert.Worker.exe"),
    [string]$SourceImage = "$env:USERPROFILE\Documents\cloud.png"
)

$ErrorActionPreference = 'Continue'

if (-not (Test-Path $WorkerPath))  { Write-Error "Worker not found: $WorkerPath"; exit 1 }
if (-not (Test-Path $SourceImage)) { Write-Error "Need a source image: $SourceImage"; exit 1 }

$work = Join-Path $env:TEMP "jalyro-convert-fuzz-$(New-Guid)"
New-Item -ItemType Directory -Path $work | Out-Null
Write-Host "Working in $work`n"

# MAX_PATH caps the whole path, not the name. Size the long-name case to
# what is left after the working directory, so it tests a long FILENAME
# rather than failing on a deep %TEMP% path.
$longName = [Math]::Max(1, [Math]::Min(200, 259 - $work.Length - 5))
if ($longName -lt 200) {
    Write-Host "[note] long-name case shortened to $longName chars to fit MAX_PATH.`n"
}

# Each case: a name, and whether producing output is acceptable.
# "refuse" means we expect a clean refusal, not a conversion.
$cases = @(
    @{ name = '-i.png';                       expect = 'convert'; why = 'leading hyphen must be a filename, not a switch' }
    @{ name = '--output.png';                 expect = 'convert'; why = 'looks like a switch' }
    @{ name = 'a;calc.exe.png';               expect = 'convert'; why = 'shell metacharacter' }
    @{ name = 'a`nb.png';                     expect = 'convert'; why = 'backtick (literal inside single quotes)' }
    @{ name = "a'quote'.png";                 expect = 'convert'; why = 'single quotes' }
    @{ name = 'a$(whoami).png';               expect = 'convert'; why = 'command substitution' }
    @{ name = 'a%TEMP%b.png';                 expect = 'convert'; why = 'environment expansion' }
    @{ name = 'file with spaces.png';         expect = 'convert'; why = 'spaces' }
    @{ name = 'café ☕ 日本語.png';             expect = 'convert'; why = 'unicode' }
    @{ name = ('x' * $longName) + '.png';     expect = 'convert'; why = 'long name' }
    @{ name = '..png';                        expect = 'convert'; why = 'leading dots' }
    @{ name = 'trailing space .png';          expect = 'convert'; why = 'space before the extension' }
    @{ name = ' leading-space.png';           expect = 'convert'; why = 'genuine leading whitespace' }
    @{ name = "trailing-space.png ";          expect = 'convert'; why = 'genuine TRAILING whitespace - Windows may strip it on create' }
)

$startedAt = Get-Date
$results = @()

foreach ($case in $cases) {
    $src = Join-Path $work $case.name
    try {
        Copy-Item -LiteralPath $SourceImage -Destination $src -Force
    } catch {
        $results += [pscustomobject]@{
            Case = $case.name; Result = 'SKIP'; Detail = "could not create: $($_.Exception.Message)"
        }
        continue
    }

    # Windows silently strips trailing spaces from filenames in many APIs, so
    # the file on disk may not be the case we intended to test. Report that
    # rather than logging a pass for a test that never happened.
    $actualName = (Get-Item -LiteralPath $src -ErrorAction SilentlyContinue).Name
    if ($actualName -ne $case.name) {
        $results += [pscustomobject]@{
            Case = $case.name; Result = 'SKIP'
            Detail = "Windows normalised the name to '$actualName'"
        }
        continue
    }

    $dst = Join-Path $work ("out-" + [guid]::NewGuid().ToString('N') + ".jpg")

    # A real timeout. Invoking the Worker directly blocks until it exits, so
    # elapsed time was only measured AFTER a hang had already finished - which
    # is not a timeout at all.
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
    $exited = $proc.WaitForExit(60000)
    if (-not $exited) {
        try { $proc.Kill($true) } catch { }
        $sw.Stop()
        $results += [pscustomobject]@{ Case = $case.name; Result = 'FAIL'; Detail = 'HUNG - killed after 60s' }
        continue
    }
    $stdout = $outTask.Result + $errTask.Result
    $code = $proc.ExitCode
    $sw.Stop()

    $produced = Test-Path -LiteralPath $dst
    $verdict = 'PASS'
    $detail = "exit $code, $($sw.ElapsedMilliseconds) ms"

    if ($sw.Elapsed.TotalSeconds -gt 60) {
        $verdict = 'FAIL'; $detail += ' - HUNG'
    }
    elseif ($case.expect -eq 'convert' -and -not $produced) {
        $verdict = 'FAIL'; $detail += " - no output: $($stdout -join ' ')"
    }
    elseif ($case.expect -eq 'refuse' -and $produced) {
        $verdict = 'FAIL'; $detail += ' - produced output when it should have refused'
    }

    $results += [pscustomobject]@{ Case = $case.name; Result = $verdict; Detail = $detail }
}

# Escape detection has to look OUTSIDE the working directory - enumerating
# files already inside it could never find an escape, so the old check was
# structurally incapable of failing.
$escaped = @()
foreach ($probe in @($env:TEMP, [Environment]::GetFolderPath('Desktop'),
                     [Environment]::GetFolderPath('MyDocuments'), $PWD.Path)) {
    if (-not $probe -or -not (Test-Path $probe)) { continue }
    $escaped += Get-ChildItem $probe -File -Filter 'out-*.jpg' -ErrorAction SilentlyContinue |
                Where-Object { $_.CreationTime -gt $startedAt }
}
# Count result failures FIRST. Adding the escape count before this line meant
# the assignment below overwrote it, so escapes printed in red and the script
# still exited 0.
$failed = ($results | Where-Object Result -eq 'FAIL').Count

if ($escaped) {
    Write-Host "`nFILES ESCAPED THE WORKING DIRECTORY:" -ForegroundColor Red
    $escaped | Select-Object FullName | Format-Table
    $failed += $escaped.Count
}

Write-Host ""
$results | Format-Table -AutoSize
if ($failed -gt 0) {
    Write-Host "$failed FAILED" -ForegroundColor Red
    exit 1          # fail the build, do not merely report
}
Write-Host "All cases passed." -ForegroundColor Green

Write-Host "`nLeaving $work in place for inspection. Remove it with:"
Write-Host "  Remove-Item '$work' -Recurse -Force"
