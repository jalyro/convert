# Why the architecture is like this

Short notes on constraints that were established the hard way. Each one has
caused a bug or a rewrite, so changing anything here without re-testing is
likely to reintroduce it.

## The shell extension

**Runs out of process.** Registered as a `com:SurrogateServer`, so it loads into
`dllhost.exe`. If it faults, Explorer is unaffected. An in-process registration
puts the code back inside Explorer.

**`GetState` must not open files.** Extension-string matching only. It runs on
Explorer's UI thread on every right-click; anything slower makes the whole
context menu feel sluggish. Measured budget: 1–3 ms warm.

**`DllMain` does nothing but store the module handle.** It runs under the loader
lock, where filesystem work and allocation can deadlock the surrogate.

**`ECF_ISSEPARATOR` renders as nothing** in a Windows 11 flyout. Group by
ordering instead.

**One submenu level.** Explorer does not support nested subcommands, so there is
no "More formats" escape hatch. Eight entries is the practical ceiling.

**The classic context menu setting hides us entirely.** If
`HKCU\Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}` exists,
no `IExplorerCommand` handler appears at all. First thing to check when the menu
is missing.

## Process identity

**A child of the COM surrogate inherits MSIX package identity**, and its AppData
writes are then virtualized into the package store.
`PROC_THREAD_ATTRIBUTE_DESKTOP_APP_POLICY` does **not** break it out — both
documented flag values were tested. Relaunching via `explorer.exe` does.

**The named-object and named-pipe namespaces are shared** across that boundary
even though the filesystem is not. That asymmetry is what the whole design rests
on: the packaged shell extension signals an unpackaged resident Host, so nothing
has to cross it.

**State lives in `%USERPROFILE%\.jalyro-convert`**, outside AppData, which MSIX
does not virtualize.

## Conversion

**libvips has no HEVC decoder** in the LGPL `NetVips.Native` build, so it cannot
read HEIC. ffmpeg decodes it instead. AVIF encoding works, because AV1 is
royalty-free and HEVC is not.

**libvips is demand-driven**, so a decode failure surfaces at the *encode* call.
Classify errors by message, not by where the exception was thrown.

**Animated HEIF is refused; multi-image HEIF collections are not detected.**
Animation is spotted by duration, which a still HEIF does not have. A
non-animated HEIF holding several images has no duration either, so only its
primary image is converted — silently. Detecting that needs a HEIF container
parser, which is not worth adding for a rare format.

**JPEG quantisation tables are stored zig-zag.** Comparing them against a
natural-order reference under-estimates quality throughout.

**Convert preserves; presets compress.** A menu item named after a format
matches the source's quality. Only a preset named after a size may discard
anything.

## Safety

**`ProcessStartInfo.ArgumentList`, never `Arguments`.** A file named `-i` is a
filename. This is the most likely vulnerability in the product.

**Validate before you mutate.** A guard placed after a rename can never fire —
the source-overwrite check once sat after the collision loop and was
unreachable.

**Sandboxing is not available.** A low-integrity Worker cannot write next to the
source file, which is the entire interaction model. See `sandboxing.md`.
