# Changelog

Pre-1.0. Versions before 0.9 were development phases and are not listed
individually.

## Unreleased
- The winget manifests carry the real installer hash and the corrected asset
  name, so `packaging/winget` is submittable rather than a template
- The installer filename no longer carries the version:
  `JalyroConvert-Setup.exe`, not `JalyroConvert-Setup-0.9.35.exe`. GitHub builds
  a permanent download URL out of the asset name, so a version in it breaks the
  website's download button at every release
- `make-installer.cmd` refuses to build when a signed installer is sitting in
  `dist\`. With the version gone from the filename, a rebuild lands on top of
  it, and a signature is a paid operation - losing one to a routine rebuild is
  not a mistake worth being able to make
- The website's download button goes straight to the installer instead of the
  release page, and says what the download weighs before you click it
- `.github/workflows/pages.yml` publishes `site/` to GitHub Pages at
  https://jalyro.github.io/convert/. A workflow rather than the branch
  setting, because Pages serves only a branch root or `/docs`, and `docs/`
  holds developer notes. No version bump: nothing that ships changed, and a
  version bump would imply a rebuild - which would imply signing again

## 0.9.35
- `build\set-publisher.cmd <thumbprint>` and `build\set-publisher.cmd /revert`
  replace the two hand-pasted one-liners that patched and restored
  `Identity/@Publisher`. Both matched one certificate subject as a literal, so
  both stopped working the moment a different certificate was used - and the
  revert is the one that fails silently, leaving a real name and address in a
  commit. The subject is now read from the store by thumbprint, XML-escaped,
  and written back with a bytewise CRLF check and a read-back comparison. The
  certificate is rejected if it has no private key, has expired, or carries an
  EKU list without Code Signing
- `build\sign-installer.cmd <thumbprint>` signs the installer. Nothing signed
  it before: `make-installer.cmd` echoed a signtool line carrying a hardcoded
  version number and left it at that. The setup .exe is the file users
  download, so its signature is the one SmartScreen shows - leaving it unsigned
  wastes every signature packed inside it. It finds the single .exe in `dist\`
  rather than composing a filename from a version held in nine places
- `install.cmd` refuses to register when a Jalyro.Convert package is already
  registered under a different publisher. The family name is a hash of the
  Publisher string, so that is a separate package: Windows registers both, both
  claim the same CLSID and verb, and the menu ends up with two entries
- The timestamp server in both signing scripts can be overridden with `TSURL`
- `get-cert-subject.cmd` prints a runnable `set-publisher.cmd` line per
  certificate instead of a manifest line to copy by hand
- `docs/signing.md`: the Publisher rule, why changing certificate changes
  package identity, what is signed and what is not (the uninstaller Inno embeds
  is not), and how a cloud certificate differs from the development one

## 0.9.34
- `stop-host.cmd` could kill an unrelated ffmpeg. Roots were compared with
  `StartsWith` and no trailing separator, so a tree beside this one -
  `jalyro-convert-old` next to `jalyro-convert` - matched, reintroducing
  exactly the harm 0.9.23 set out to prevent
- Build scripts no longer interpolate `%ROOT%`, `%STAGE%`, `%LOGS%` or
  `%~dp0` into single-quoted PowerShell literals. A path such as
  `C:\Users\O'Brien\...` closed the string early and the command would not
  parse. Paths now reach PowerShell through the environment
- `uninstall.cmd` ignored a failed `stop-host.cmd` and a failed
  `Remove-AppxPackage`, then printed UNINSTALLED regardless
- `pscheck.py` claimed to check quote balance but reported an unterminated
  string as structurally OK. It now reports the opening quote and stops
  there, since every later brace count is meaningless. Selftest is 15
  assertions
- CodeQL no longer analyses C++. Its tracer never observed the compiler:
  the database finalized empty under `build-mode: undefined`, `manual`, and
  with the vcvars nesting removed. CodeQL traces MSBuild-driven builds and
  this project compiles with direct `cl.exe` calls by design. A `.vcxproj`
  written only for CodeQL would analyse something other than what ships.
  The C# Worker, where untrusted bytes are parsed, is still analysed
- The hardcoded Visual Studio path from the 0.9.33 diagnostic is gone

## 0.9.33
- TEST: the CodeQL C++ step calls `vcvars64.bat` directly instead of going
  through `ci-vcvars.cmd`, to find out whether the extra batch nesting is
  what loses the CodeQL tracer. The hardcoded Visual Studio path is what
  0.9.25 removed on purpose; this is one diagnostic run, not a keeper

## 0.9.32
- CodeQL `init` now sets `build-mode: manual`. The C++ database finalized
  with "detected code written in C/C++ but could not process any of it" -
  the build between `init` and `analyze` ran untraced while the action
  reported the build mode as `undefined`. The C# leg was unaffected

## 0.9.31
- The dev certificate subject no longer carries a locality (`L=`). It is
  personal data and this repository is public; the subject keeps `O=` and
  `C=` so it stays multi-RDN and the Publisher-matching rule still bites.
  An existing "Jalyro Dev" certificate does not need regenerating - the
  release loop reads the subject from the certificate store

## 0.9.30
- CodeQL runs on push, pull request and a weekly schedule again, now that
  the repository is public. It had been manual-only because code scanning
  on a private repository needs Advanced Security

## 0.9.29
- CI actions bumped off the deprecated Node.js 20 runtime, each major
  boundary checked against this project's usage before bumping:
  `actions/checkout` v4 to v7, `actions/setup-dotnet` v4 to v6,
  `actions/upload-artifact` v4 to v7, `github/codeql-action` v3 to v4.
  Nothing here uses the affected features (fork-PR checkout under
  `pull_request_target`, pre-10 .NET installers, the new `archive` input)
- Removed `microsoft/setup-msbuild` from the build workflow. Nothing invokes
  MSBuild: the C++ is compiled by direct `cl.exe` calls via `ci-vcvars.cmd`,
  and `dotnet publish` uses the SDK's own MSBuild. Left over from before
  `ci-vcvars.cmd` existed
- `uninstall.cmd` now stops the Host before `Remove-AppxPackage`, like
  `unlock.cmd` already did; removal had taken ~30 seconds with the Host
  still running

## 0.9.28
- `pscheck.py` now extracts and checks the PowerShell embedded in `.cmd`
  caret-continued strings. It only read `.ps1` files, so most build scripts
  shipped with their PowerShell completely unchecked - the two 5.1-only API
  bugs fixed in 0.9.19 lived in exactly such blocks
- Blocks invoked via `powershell` are additionally checked against a short
  list of things Windows PowerShell 5.1 cannot run: `Process.Kill($true)`,
  `ProcessStartInfo.ArgumentList`, `&&`/`||`, `-Parallel`, and any `#`
  outside a string (the chunks join onto one line at runtime, so a `#`
  comments out everything after it - the old fetch-ffmpeg failure). `pwsh`
  blocks and `#requires -Version 7` scripts are exempt
- A built-in `--selftest` (11 assertions) proves the extractor extracts,
  joins and unescapes rather than silently returning empty; CI runs it, then
  extracts every block with `--extract` and feeds them through the same real
  `Parser::ParseFile` that already checks the `.ps1` files

## 0.9.27
- The redist check reported failure while printing its own success message.
  `findstr` sets ERRORLEVEL 1 when it finds nothing, which is the passing
  case here, and `echo` does not reset it - so the step ended carrying a 1.
  It now exits explicitly

## 0.9.26
- The COM export and redist checks failed with `dumpbin is not recognized`.
  Every workflow `run:` block is its own process, so the toolchain the
  build step set up was gone by the next step. They had always been broken;
  the build failing first meant they never ran
- The toolset lookup moved into `build\ci-vcvars.cmd`, called by every step
  that needs `cl`, `link`, `rc` or `dumpbin`, instead of being repeated
  inline in each workflow

## 0.9.25
- CI could not build. The workflow called `vcvars64.bat` from hardcoded
  Visual Studio 2022 paths; GitHub moved `windows-latest` to Visual Studio
  2026 in June 2026, so both paths missed and `cl.exe` was never on PATH -
  cmd then found Git's GNU `link` instead of the linker. Both workflows now
  locate the toolset with `vswhere`, which does not care about the year
- CodeQL is manual-only until the repository is public. Code scanning needs
  Advanced Security on a private repository, so every run failed before
  analysing anything
- CodeQL builds the shell extension explicitly rather than through
  `autobuild`, which needs an MSBuild project or solution and this project
  has neither

## 0.9.24
- Added `.gitattributes`. Every file here is authored CRLF; without it the
  line endings in a clone depend on each developer's `core.autocrlf`
  setting rather than on the repository

## 0.9.23
- `stop-host.cmd` killed every process named `ffmpeg`, including a user's
  own unrelated encode. It now matches ffmpeg by path against the repo
  root and the install directory, as `Program.cs` already did for its
  orphan sweep. The two Jalyro process names are distinctive and still
  matched by name
- `unlock.cmd` force-killed every `dllhost.exe` in the session, taking
  unrelated thumbnail handlers, preview handlers and shell extensions
  with it. It now kills only a surrogate with a module loaded from
  `stage\`. A surrogate that cannot be inspected is left alone and named
  by the check that follows, rather than killed blind
- The manifest header asked for two placeholders to be replaced when only
  `Publisher` is one. The `PublisherDisplayName` guard in
  `make-package.cmd` is kept for forks, with that reason recorded
- `FormatTable.cs` called the image quality numbers FLOORS and then gave
  an example below the floor. They are defaults for a lossless source; a
  JPEG source is matched to its own quality and can come out lower.
  Runtime behaviour, `Settings.cs` and the README were already correct

## 0.9.22
- A batch that failed or was cancelled drew a full green progress bar
  above the words "n failed". The taskbar error state was already being
  set, so only half the outcome was shown; the bar now turns red with it.
  It still fills, since the work did finish - the colour, not the fill,
  reports the result

## 0.9.21
- The Worker had no application manifest, so it was not long-path aware
  while the Host was. That put the flag on the process with no UI and no
  user files, and left it off the one that opens them: a source path over
  MAX_PATH failed to decode. It now carries `longPathAware`, which also
  needs `LongPathsEnabled=1` in the registry to take effect

## 0.9.20
- README documents that behavioural antivirus engines may flag
  `build\fetch-ffmpeg.ps1` and delete it. Kaspersky's System Watcher
  detects `PDM:Trojan.Win32.Generic` by behaviour analysis, terminates
  PowerShell mid-run and removes both fetch scripts from the working copy.
  The script cannot report this itself, since it is killed first. Covers
  the exclusion needed and a manual alternative that skips the script

## 0.9.19
- `stop-host.cmd` reported stopping the Host without stopping it.
  `Process.Kill($true)` is a .NET Core overload and does not exist on
  Windows PowerShell 5.1, where an empty catch swallowed the error. It
  now uses `Stop-Process`
- `timeout /t N /nobreak` reads and discards the console input buffer, so
  commands pasted after `unlock.cmd` or `restart-explorer.cmd` - or after
  `make-package.cmd` and `install.cmd`, which call them - were silently
  eaten. Replaced with a `ping` delay
- Both fuzz harnesses now declare `#requires -Version 7`. Under Windows
  PowerShell 5.1 they reported a full table of failures without ever
  passing an argument to the Worker, because
  `ProcessStartInfo.ArgumentList` does not exist on .NET Framework
- The filename fuzz long-name case sizes itself to its working directory.
  Fixed at 200 characters it exceeded MAX_PATH from a `%TEMP%` working
  directory, failing on path length while claiming to test name length
- CONTRIBUTING records the PowerShell 7 requirement, previously implicit
  in the CI workflow only

## 0.9.18
- Internal identifiers aligned with the Jalyro name: the C++ shell
  namespace, the manifest verb id, and the temp-file prefixes. No
  behaviour changes beyond identifiers and comments
- Temp-file prefixes changed in lockstep across the Worker (writes them),
  Host cleanup (globs them) and both fuzz scripts (assert on them):
  temporaries beside the output are now `.jalyro-convert-*`, `%TEMP%`
  intermediates are `jalyro-convert-*`, and the writability probe is
  `.jalyro-convert-write-test-*`
- Temporaries left by an earlier version are not swept by a transition
  glob, deliberately: Host cleanup only ever targets the process id of a
  Worker it killed in the current session, so a glob for an older prefix
  keyed to a new Worker's pid could never match anything. Any strays were
  already unreachable under the previous semantics and are inert; delete
  them manually if found

## 0.9.17
- The third exit-code-3 refusal (multi-frame GIF/WEBP/AVIF and multi-page
  TIFF) also showed the generic "output format is not supported" dialog; it
  now states the real reason, same class as the 0.9.15 HEIF fix

## 0.9.16
- The single-file failure dialog opened behind the focused window: a
  background process has no foreground rights and the ownerless MessageBox
  had nothing to sit on. It is now owned by a transient topmost window
- Every conversion logged "Rejected job path outside the spool" for the
  Host's own claim rename: the spool watcher raises a rename event when
  either name matches `*.job`, so `X.job -> X.job.claimed` echoed back in.
  The echo is now ignored, and the rejection log names the actual rule

## 0.9.15
- The dialog for Worker exit code 3 showed "that output format is not
  supported" for three different conditions. The two runtime refusals now
  show their real reason: animated HEIF, and ffmpeg missing at runtime
- `show-log.cmd` searched pre-storage-move AppData paths and could never find
  anything; it now reads `%USERPROFILE%\.jalyro-convert\logs`
- `PublisherDisplayName` ships as "Sprenkels Media" instead of a placeholder;
  it is cosmetic and public, unlike the certificate Subject which remains a
  per-machine input

## 0.9.14
- Development certificate renamed: `make-dev-cert.cmd` created a
  certificate under an outdated name. It now creates "Jalyro Dev"

## 0.9.13
- ffmpeg runnability checks drain all output before selecting the version
  line. Piping a native command into `Select-Object -First 1` stops it early
  and can leave `$LASTEXITCODE` unset (PowerShell #19848); `$null -ne 0` then
  rejected a valid download. The same construct in the CI provenance step -
  which runs under PowerShell 7, where the behaviour is confirmed - could
  conversely let the step pass without ffmpeg ever running

## 0.9.12
- A swap interrupted by process kill or power loss is repaired at the start of
  the next fetch: the stranded backup is restored before anything may delete
  it. The 0.9.11 rollback only covered failures inside a single run
- A downloaded ffmpeg that cannot start is refused before the swap rather than
  installed with a warning; existence and hash checks alone could have
  released a pinned but unusable binary
- Multi-image HEIF limitation stated in the user-facing README, not only in
  developer documentation

## 0.9.11
- `fetch-ffmpeg` rewritten as a real PowerShell script. The previous version
  built ~30 lines of PowerShell from caret-continued cmd strings, and a `#`
  comment inside it commented out everything after it on the joined line —
  including the step that installed the download. It reported success and
  installed nothing.
- ffmpeg install now swaps with a backup and rolls back on failure, so a working
  installation is never lost. The previous claim of atomicity was too strong.
- Animated HEIF detected at any duration; the 0.1 s threshold let a two-frame
  sequence through as a still. Multi-image HEIF collections remain undetected —
  documented rather than implied.
- Dangling `decisions.md #N` references removed
- CI parse-checks every PowerShell script, not only the fuzz harnesses

## 0.9.10
- HEIF frame check applied to HEIF only; it was rejecting ordinary
  video-to-image conversions
- `gpl-shared` ffmpeg variant refused rather than installing a build that
  cannot start
- ffmpeg installed atomically, so a failed copy no longer leaves none at all
- Pipe shutdown drains completely instead of timing out after 30 seconds
- Documentation reduced

## 0.9.9
- ffmpeg hash verified *before* installation, again at packaging, and required
  for release builds
- Multi-frame HEIF detected
- JPEG estimator test drives the real implementation via `--jpeg-quality`
- `JpegQuality` scans past the first DQT segment

## 0.9.8
- **JPEG quality estimation corrected**: quantisation tables are stored
  zig-zag, and were being compared against a natural-order reference
- Multi-frame images refused rather than silently reduced to one frame
- Settings wording corrected — quality values are not minimums for JPEG sources
- Pipe drains on shutdown; oversize payloads rejected, not truncated

## 0.9.7
- One-shot windows no longer hold the resident singleton
- Temporary cleanup after any failed Worker exit, not only cancellation
- Pipe uses an ordered channel with a bounded read and a timeout
- PowerShell parse check in CI

## 0.9.6
- Settings and diagnostics no longer kill the running Host's conversions
- `--identity` opens its window while a Host is running
- Cancelled jobs produce an outcome for every input

## 0.9.0 – 0.9.5
Extensive review-driven fixes across process lifetime, concurrency, output
naming, size targets, image quality and the build scripts.

## 0.1 – 0.8 (development phases)
Shell extension and sparse packaging; installer and resident Host; images via
libvips; audio, video and HEIC via ffmpeg; batch queue with progress and
cancellation; presets and settings; hardening; distribution.
