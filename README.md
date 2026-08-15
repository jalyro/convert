# Jalyro Convert

Convert images, audio and video from the Windows 11 right-click menu.

Select one file or two hundred, right-click, pick a format. The converted files
appear beside the originals. Nothing is uploaded anywhere.

**Windows 11 22H2 or later.**

---

## Formats

| | In | Out |
|---|---|---|
| Images | JPG, PNG, WEBP, AVIF, HEIC, HEIF, TIFF, BMP, GIF | JPG, PNG, WEBP, AVIF, TIFF |
| Video | MP4, MOV, MKV, WEBM, AVI, M4V, WMV, FLV, MPG | MP4, WEBM |
| Audio | MP3, WAV, FLAC, M4A, AAC, OGG, OPUS, WMA | MP3, WAV, FLAC, M4A, OPUS |

**HEIC works without the paid Microsoft Store HEVC extension.**

## Behaviour worth knowing

**Quality is preserved, not fixed.** Converting from a JPEG matches whatever it
was saved at — re-encoding an already-compressed photo higher cannot recover
detail, it only makes a bigger file. PNG and TIFF output are lossless, as is
PNG to WEBP.

**MOV to MP4 usually copies rather than re-encodes**, since both hold the same
codecs. Lossless, and takes seconds.

**Compress for email** and **Discord-friendly** work backwards from a real size
limit rather than guessing at settings.

**A HEIC/HEIF file holding several photos converts only the main one.** Bursts
and similar multi-image containers are not detected; the extra images are not
extracted. Animated HEIF is refused rather than flattened to one frame.

**One image converts silently.** Batches show progress and can be cancelled.
Nothing is ever overwritten — collisions become `photo (1).jpg`.

---

## Building

Windows 11, Visual Studio 2026 with **Desktop development with C++** and the
**Windows 11 SDK**, the **.NET 10 SDK**, and Inno Setup 6 for the installer.
Everything runs from the **x64 Native Tools Command Prompt**.

```bat
build\check-prereqs.cmd
build\make-dev-cert.cmd     :: once - Windows will not install an unsigned MSIX
build\fetch-ffmpeg.cmd      :: once - ffmpeg is not committed
```

### Your antivirus may delete the fetch script

`build\fetch-ffmpeg.ps1` downloads an executable and then runs it to check
it starts. That is the behaviour of a downloader trojan, and behavioural
engines score actions rather than intent. Kaspersky's System Watcher flags
it as `PDM:Trojan.Win32.Generic`, terminates PowerShell, and **deletes both
`fetch-ffmpeg.ps1` and `fetch-ffmpeg.cmd` from your working copy**. Other
products with behavioural heuristics may do the same.

It is a false positive. The detection is on the process, not the file:
cloning is safe and a static scan finds nothing. Only running the fetch
triggers it.

The script gives no warning when this happens, because it is killed before
it can report anything. The symptom is `fetch-ffmpeg.cmd` failing with a
missing-file error on the next run.

Two ways round it:

- Exclude your clone directory in your antivirus. In Kaspersky the
  exclusion must cover **all protection components**, not only File
  Anti-Virus - System Watcher is what fires here.
- Or skip the script. Download the `win64-gpl` build from
  <https://github.com/BtbN/FFmpeg-Builds/releases>, put `ffmpeg.exe` in
  `src\ffmpeg\`, and copy the licence files beside it. Verify what you
  downloaded:

  ```bat
  certutil -hashfile src\ffmpeg\ffmpeg.exe SHA256
  ```

  and put that hash in `build\ffmpeg-expected.sha256`, which is how the
  build verifies the binary from then on.

Put your certificate's exact Subject in `Publisher` in
`package\AppxManifest.xml` (`build\get-cert-subject.cmd` prints it), then:

```bat
build\build.cmd
build\make-package.cmd
build\sign.cmd <thumbprint>
build\install.cmd
```

`CONTRIBUTING.md` lists the traps. `docs/decisions.md` explains why the
architecture is shaped the way it is — read it before changing anything
structural.

## Licence

MIT. Bundles **ffmpeg** under the GPL and **libvips** under the LGPL; see
`THIRD-PARTY-NOTICES.md` for the obligations that come with distributing it.

Made by Petrus Sprenkels.
