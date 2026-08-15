using System;
using System.Collections.Generic;

namespace Jalyro.Convert.Host;

/// <summary>
/// What converts to what.
///
/// Compiled rather than JSON on purpose: the shell extension needs the same
/// knowledge on Explorer's UI thread, where parsing a config file is exactly
/// the kind of work that makes a context menu feel slow. The C++ side keeps its
/// own copy in Common.cpp; the two must agree.
/// </summary>
internal static class FormatTable
{
    public enum Kind { Unsupported, Image, Video, Audio }

    public static readonly HashSet<string> ImageInputs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".avif",
        ".heic", ".heif", ".bmp", ".tif", ".tiff", ".gif"
    };

    public static readonly HashSet<string> VideoInputs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".mkv", ".webm", ".avi", ".m4v", ".wmv", ".flv", ".mpg", ".mpeg"
    };

    public static readonly HashSet<string> AudioInputs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".wma"
    };

    public static Kind KindOf(string extension)
    {
        if (ImageInputs.Contains(extension)) return Kind.Image;
        if (VideoInputs.Contains(extension)) return Kind.Video;
        if (AudioInputs.Contains(extension)) return Kind.Audio;
        return Kind.Unsupported;
    }

    public static bool IsSupportedInput(string extension) => KindOf(extension) != Kind.Unsupported;

    /// <param name="Extension">Output file extension, with the dot.</param>
    /// <param name="Quality">1-100, engine-specific meaning. 0 means lossless/default.</param>
    /// <param name="MaxEdge">Long-edge cap in pixels. 0 means no resize.</param>
    /// <param name="TargetBytes">
    /// Hard size ceiling for the output, or 0 for none. When set, the video
    /// path solves a bitrate from the file's duration and encodes two-pass,
    /// then verifies the result actually fits. A preset named after a size
    /// limit has to honour it.
    /// </param>
    public sealed record Target(string Extension, int Quality, int MaxEdge, long TargetBytes = 0);

    private static readonly Dictionary<string, Target> Targets =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Images. No HEIC output: it needs x265 (GPL), and nobody asks for it.
            //
            // Defaults for a LOSSLESS source, not floors. A JPEG source is
            // matched to its own quality instead, which can land below the
            // number here: a quality-60 JPG comes out around 62, because
            // raising it to 90 cannot recover detail. PNG to WEBP is
            // lossless outright. See Settings.JpegQuality and VipsEngine.
            //
            // The principle: a menu item named after a format converts and
            // preserves. A preset named after a size compresses. Only the
            // second is allowed to discard anything the user did not ask about.
            ["jpg"]   = new Target(".jpg",  90, 0),
            ["png"]   = new Target(".png",   0, 0),
            ["webp"]  = new Target(".webp", 90, 0),
            ["avif"]  = new Target(".avif", 80, 0),

            // TIFF: narrow appeal, but the people who need it need it
            // absolutely - print, scanning, and archival submission. Deflate
            // compressed, so lossless at a fraction of uncompressed size.
            //
            // BMP is NOT offered as an output: this libvips build has no BMP
            // writer. It could be routed through ffmpeg, but BMP is
            // uncompressed and strictly worse than PNG in every dimension,
            // and nothing that reads BMP fails to read PNG. It remains a
            // supported INPUT.
            ["tiff"]  = new Target(".tiff",  0, 0),

            // Video.
            ["mp4"]   = new Target(".mp4",  70, 0),
            ["webm"]  = new Target(".webm", 70, 0),

            // Audio, and extract-audio from video.
            ["mp3"]   = new Target(".mp3",   0, 0),
            ["wav"]   = new Target(".wav",   0, 0),
            ["flac"]  = new Target(".flac",  0, 0),
            ["m4a"]   = new Target(".m4a",   0, 0),
            ["opus"]  = new Target(".opus",  0, 0),

            // Presets.
            //
            // The image one is a quality judgement. The video ones are SIZE
            // PROMISES and are implemented as such - v0.5.2 shipped fixed
            // quality with no reference to duration, so a 30-second clip came
            // out needlessly small and a 10-minute clip blew past the limit.
            ["email"]    = new Target(".jpg", 78, 2048),

            // Most mail servers reject attachments over 25 MB; 20 leaves room
            // for base64 expansion and headers.
            ["compress"] = new Target(".mp4", 0, 1080, 20L * 1024 * 1024),

            // Discord's free tier caps uploads at 10 MB.
            ["discord"]  = new Target(".mp4", 0, 720, 10L * 1024 * 1024)
        };

    public static bool TryResolve(string verb, out Target target)
        => Targets.TryGetValue(verb, out target!);

    /// <summary>
    /// Applies user settings over the built-in defaults. Called once at Host
    /// start and again whenever settings are saved.
    /// </summary>
    public static void ApplySettings(Settings s)
    {
        s.Validate();

        Targets["jpg"]   = new Target(".jpg",  s.JpegQuality, 0);
        Targets["webp"]  = new Target(".webp", s.WebpQuality, 0);
        Targets["avif"]  = new Target(".avif", s.AvifQuality, 0);

        Targets["email"] = new Target(".jpg", Math.Max(1, s.JpegQuality - 7), s.EmailImageMaxEdge);

        Targets["compress"] = new Target(".mp4", 0, 1080,
            (long)s.EmailVideoMegabytes * 1024 * 1024);
        Targets["discord"] = new Target(".mp4", 0, 720,
            (long)s.DiscordMegabytes * 1024 * 1024);
    }
}
