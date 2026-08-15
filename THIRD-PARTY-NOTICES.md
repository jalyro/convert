# Third-party notices

Jalyro Convert's own source is MIT (see `LICENSE`). It bundles and invokes
third-party components under their own terms, listed here.

**Read this before the first public release.** Getting licence obligations
wrong in public is much harder to undo than getting them right first.

---

## ffmpeg — GPL-2.0-or-later (as bundled)

**What it does here:** all audio and video conversion, plus HEIC decoding.

**Why GPL and not LGPL:** ffmpeg is LGPL-2.1-or-later by default, but H.264
output requires **libx264**, which is GPL-2.0-or-later. Enabling it places the
whole ffmpeg build under the GPL. MP4 output is half the point of the video
feature, so the GPL build is what ships.

**How this coexists with MIT code:** Jalyro Convert executes `ffmpeg.exe`
as a **separate program** over its documented command-line interface. It does
not link ffmpeg, does not share an address space with it, and does not
incorporate its source. This is the standard aggregation position that
HandBrake, VLC and other shell-out tools rely on.

**Obligations when distributing:**

1. Ship ffmpeg's licence text alongside the binary — `build/fetch-ffmpeg.cmd`
   copies it into `src/ffmpeg/` automatically.
2. State clearly in the installer and on the website that ffmpeg is included
   under the GPL.
3. **Honour the source offer.** Link to the exact build that shipped, and to
   its corresponding source. Record the build URL and date in each release's
   notes — "we downloaded it from a CI server" is not by itself sufficient if
   that build later disappears.

- Project: https://ffmpeg.org
- Licence: https://ffmpeg.org/legal.html
- Builds used: https://github.com/BtbN/FFmpeg-Builds

## libvips — LGPL-2.1-or-later

**What it does here:** all still-image conversion.

Distributed through the `NetVips.Native.win-x64` NuGet package as a
**statically linked** binary. LGPL static linking carries a relinking
obligation — recipients must be able to substitute a modified libvips. Since
this project is open source and buildable from the published sources, that is
satisfied by the repository itself.

**Known limitation:** the LGPL build ships libheif **without an HEVC decoder**,
because HEVC carries patent obligations that AV1 does not. HEIC therefore
decodes via ffmpeg instead. See `docs/decisions.md`.

- Project: https://github.com/libvips/libvips
- Package: https://www.nuget.org/packages/NetVips.Native.win-x64/

## NetVips — MIT

Managed bindings for libvips.

- https://github.com/kleisauke/net-vips

## Inno Setup — modified BSD-style

Used to build the installer. Not redistributed as part of the product.

- https://jrsoftware.org/isinfo.php

---

## Patent note, not legal advice

H.264 and H.265 carry patent obligations entirely separate from software
licensing. For a free, open-source tool distributed as source and binaries,
this is the same posture as HandBrake, VLC and every other ffmpeg-based project
on GitHub. Be aware it exists; do not build a business model that assumes it is
irrelevant.
