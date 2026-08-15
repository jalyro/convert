# Contributing

## Building

Requires Windows 11, Visual Studio 2026 with **Desktop development with C++**
and the **Windows 11 SDK**, the **.NET 10 SDK**, and Inno Setup 6 for the
installer.

```bat
build\check-prereqs.cmd
build\make-dev-cert.cmd      :: once - Windows will not install an unsigned MSIX
build\fetch-ffmpeg.cmd       :: once - ffmpeg is not committed
```

Then edit `package\AppxManifest.xml` to carry your certificate's exact Subject
in `Publisher`, and:

```bat
build\build.cmd
build\make-package.cmd
build\sign.cmd <thumbprint>
build\install.cmd
```

Everything must run from the **x64 Native Tools Command Prompt for VS 2026**.

The fuzz harnesses in `tools/fuzz/` need **PowerShell 7** (`pwsh`), not the
Windows PowerShell 5.1 that ships with Windows. They use
`ProcessStartInfo.ArgumentList`, which does not exist on .NET Framework; under
5.1 they used to report a full table of failures without ever passing an
argument to the Worker. Both now declare `#requires -Version 7` and refuse to
start instead.

## Things that will bite you

Collected from real debugging sessions; each one cost hours.

- **`stage\` is what runs, not `out\`.** `build.cmd` writes to `out\`;
  `make-package.cmd` copies to `stage\`. Forgetting the second step means
  testing the previous build.
- **Stop the Host before staging or signing.** A running Host, Worker or ffmpeg
  holds files open. `stop-host.cmd` is called automatically, but a stray
  process still causes sharing violations.
- **Unregister before replacing the shell DLL.** The COM surrogate holds it
  open. `build\uninstall.cmd`.
- **The classic context menu override hides the extension entirely.** If the
  menu never appears, run `build\check-menu-mode.cmd`.
- **`AppxManifest.xml` `Publisher` must match your certificate Subject
  character for character.** Copy it from `get-cert-subject.cmd`; never retype.

## What CI checks, and what it does not

`.github/workflows/build.yml` compiles the shell extension, the Host and the
Worker, verifies the COM exports and the absence of a VC++ redist dependency,
confirms the CLSID matches between manifest and source, and runs both fuzz
harnesses.

**None of that catches logic errors.** Two review passes on v0.9.0 found 29
issues — every one of them semantic, and every one in code that compiled
cleanly: a cleanup routine that killed unrelated processes, jobs dispatched
twice, timeouts that could never fire, settings that were saved and ignored.

If you are changing anything that touches **concurrency, process lifetime, or a
promise the UI makes** (a size limit, a progress figure, a refusal), trace the
failure path by hand. A green build says the code is well-formed, not that it
is right.

## Checking the PowerShell

There is no PowerShell on the machine these releases are authored on, so the
fuzz scripts ship unparsed. `tools/pscheck.py` is a crude structural check —
brace balance, and `elseif`/`else` following a closing brace — that exists
because an orphaned `elseif` once shipped and would have made the CI fuzz step
fail without running a single case.

It also extracts the PowerShell embedded in `.cmd` caret-continued strings and
checks each block the same way, plus a small list of things that break under
the Windows PowerShell 5.1 host: `Process.Kill($true)` and
`ProcessStartInfo.ArgumentList` (both .NET Core only — both shipped, both
failed silently), `&&`/`||`, `-Parallel`, and a `#` in a block (the chunks join
onto one line at runtime, so it comments out everything after it). Blocks
invoked via `pwsh`, and `.ps1` files with `#requires -Version 7`, are exempt
from the 5.1 list. `python tools\pscheck.py --selftest` proves the extractor
extracts; `--extract DIR` writes each block out as a `.ps1`.

CI does a real `Parser::ParseFile` — on the `.ps1` files and on the extracted
blocks — before running anything, which is the check that counts.

## Architecture

Read `docs/decisions.md` first. Those constraints are not trivia — each one has
already caused a bug or a rewrite.

The short version:

- The shell extension does **no** media work. It writes a job file and signals
  a resident Host over a named pipe. It must return in well under 50 ms.
- The Host is unpackaged and started at login, so its storage is not
  virtualized by MSIX.
- Each conversion runs in its own Worker process, because that is where
  untrusted bytes get parsed.

## Non-negotiables

- **`GetState` never opens a file.** Extension-string matching only. The moment
  it touches disk, we join the group of extensions that make Windows' context
  menu feel slow.
- **`ProcessStartInfo.ArgumentList`, never `Arguments`.** A file named `-q` is
  a filename. This is the most likely vulnerability in the product.
- **Validate before you mutate.** A guard placed after a rename can never fire
  — see `docs/decisions.md`.
- **Never overwrite a source file**, and never leave a partial output on disk.
