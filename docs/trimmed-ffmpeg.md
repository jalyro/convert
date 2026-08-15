# Trimming ffmpeg

The stock build is ~80 MB and pushes the installer past 200 MB. A build
configured for only the formats this product actually converts gets to roughly
30 MB — and, more usefully, **removes parsers we never call**. Fewer demuxers
is a smaller attack surface, not just a smaller download.

## Status

Not done. The stock build ships today. This is Phase 5 work.

## What must survive

Everything in `src/Host/FormatTable.cs`. If the two ever disagree, conversions
fail at run time with an unhelpful error, so treat that file as the spec.

| Need | Component |
|---|---|
| MP4 output | libx264 (GPL) |
| WEBM output | libvpx-vp9, libopus |
| MP3 | libmp3lame |
| FLAC, WAV | built in |
| M4A | native AAC encoder |
| Video decode | h264, hevc, vp8, vp9, av1, mpeg4, wmv, flv, mjpeg |
| Audio decode | aac, mp3, flac, vorbis, opus, wmav2, pcm |
| **HEIC decode** | hevc decoder + mov demuxer — the marquee feature |
| Containers | mov, mp4, matroska, webm, avi, mpegts, asf, flv, mp3, wav, flac, ogg |

## Configure sketch

Not yet validated. Build with MSYS2 + mingw-w64.

```sh
./configure \
  --toolchain=msvc --target-os=win64 --arch=x86_64 \
  --enable-gpl --enable-libx264 --enable-libvpx --enable-libopus \
  --enable-libmp3lame \
  --disable-everything \
  --enable-decoder=h264,hevc,vp8,vp9,av1,mpeg4,wmv3,flv,mjpeg,png \
  --enable-decoder=aac,mp3,flac,vorbis,opus,wmav2,pcm_s16le,alac \
  --enable-encoder=libx264,libvpx_vp9,libopus,libmp3lame,aac,flac,pcm_s16le,png \
  --enable-demuxer=mov,matroska,avi,mpegts,asf,flv,mp3,wav,flac,ogg,image2 \
  --enable-muxer=mp4,webm,mp3,wav,flac,ipod,ogg,image2 \
  --enable-protocol=file \
  --enable-filter=scale,format,aformat,anull,null \
  --enable-parser=h264,hevc,vp9,av1,aac,mpegaudio,flac,opus \
  --disable-doc --disable-ffplay --disable-network
```

`--disable-network` is worth keeping regardless of size: the product pins
`-protocol_whitelist file`, so network protocols are dead weight and pure risk.

## Before switching

Re-run the whole matrix in the changelog. A missing decoder shows up as a
conversion that used to work and silently stops — exactly how the libheif HEVC
gap was found.
