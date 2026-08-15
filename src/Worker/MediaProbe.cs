using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace Jalyro.Convert.Worker;

/// <summary>
/// What is inside a media file: duration, codecs, dimensions.
///
/// Needed for two decisions:
///   - whether a stream copy is possible, so MOV to MP4 stays lossless
///   - solving a bitrate against a size target, so "Discord-friendly" is
///     actually under 10 MB rather than a guess
///
/// Reads ffmpeg's own stderr rather than shipping ffprobe. The static ffprobe
/// binary is 138 MB - half the entire ffmpeg footprint - for information
/// ffmpeg already prints when asked to open a file with no output. Parsing
/// text is less elegant than ffprobe's JSON, and 138 MB is a lot to pay for
/// elegance in a tool whose pitch is "right-click and convert".
/// </summary>
internal sealed record MediaInfo(
    double DurationSeconds,
    string VideoCodec,
    string AudioCodec,
    int Width,
    int Height)
{
    public bool HasVideo => VideoCodec.Length > 0;
    public bool HasAudio => AudioCodec.Length > 0;
}

internal static class MediaProbe
{
    // "  Duration: 00:03:32.15, start: 0.000000, bitrate: 12345 kb/s"
    private static readonly Regex DurationPattern = new(
        @"Duration:\s*(\d+):(\d{2}):(\d{2}(?:\.\d+)?)",
        RegexOptions.Compiled);

    // "  Stream #0:0(eng): Video: h264 (High) (avc1 / 0x31637661), yuv420p, 1920x1080"
    private static readonly Regex VideoPattern = new(
        @"Stream #\d+:\d+.*?:\s*Video:\s*([A-Za-z0-9_]+)",
        RegexOptions.Compiled);

    private static readonly Regex AudioPattern = new(
        @"Stream #\d+:\d+.*?:\s*Audio:\s*([A-Za-z0-9_]+)",
        RegexOptions.Compiled);

    // Dimensions appear on the video stream line. Bounded to plausible values
    // so a codec fourcc or a bitrate cannot be mistaken for a resolution.
    private static readonly Regex DimensionPattern = new(
        @"\b(\d{2,5})x(\d{2,5})\b",
        RegexOptions.Compiled);


    public static MediaInfo? Probe(string input)
    {
        if (!FFmpegEngine.IsAvailable)
            return null;

        var psi = new ProcessStartInfo
        {
            FileName = FFmpegEngine.ExecutablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        // Same argument discipline as every other invocation: separate list
        // elements, file: prefix, protocol whitelist pinned.
        psi.ArgumentList.Add("-nostdin");
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-protocol_whitelist");
        psi.ArgumentList.Add("file");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add("file:" + input);

        // No output file, so ffmpeg prints the stream summary and exits with
        // "At least one output file must be specified" - a non-zero code that
        // is the expected outcome here, not a failure.

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
                return null;

            // Async drain then wait. Reading to end first would block past the
            // timeout if ffmpeg hung without closing its handles.
            System.Threading.Tasks.Task<string> err = process.StandardError.ReadToEndAsync();
            System.Threading.Tasks.Task<string> outp = process.StandardOutput.ReadToEndAsync();

            if (!process.WaitForExit(60_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }

            System.Threading.Tasks.Task.WaitAll(
                new System.Threading.Tasks.Task[] { err, outp }, 5000);

            return err.IsCompletedSuccessfully ? Parse(err.Result) : null;
        }
        catch
        {
            return null;
        }
    }

    private static MediaInfo? Parse(string text)
    {
        if (string.IsNullOrEmpty(text))
            return null;

        double duration = 0;
        Match d = DurationPattern.Match(text);
        if (d.Success
            && int.TryParse(d.Groups[1].Value, out int hours)
            && int.TryParse(d.Groups[2].Value, out int minutes)
            && double.TryParse(d.Groups[3].Value, NumberStyles.Float,
                               CultureInfo.InvariantCulture, out double seconds))
        {
            duration = hours * 3600 + minutes * 60 + seconds;
        }

        string videoCodec = string.Empty;
        int width = 0, height = 0;

        Match v = VideoPattern.Match(text);
        if (v.Success)
        {
            videoCodec = v.Groups[1].Value;

            // Look for dimensions on that stream's line only.
            int lineStart = text.LastIndexOf('\n', Math.Min(v.Index, text.Length - 1)) + 1;
            int lineEnd = text.IndexOf('\n', v.Index);
            if (lineEnd < 0) lineEnd = text.Length;

            Match dim = DimensionPattern.Match(text[lineStart..lineEnd]);
            if (dim.Success)
            {
                int.TryParse(dim.Groups[1].Value, out width);
                int.TryParse(dim.Groups[2].Value, out height);
            }
        }

        Match a = AudioPattern.Match(text);
        string audioCodec = a.Success ? a.Groups[1].Value : string.Empty;


        // Nothing recognised at all means this is not media we can reason about.
        if (duration <= 0 && videoCodec.Length == 0 && audioCodec.Length == 0)
            return null;

        return new MediaInfo(duration, videoCodec, audioCodec, width, height);
    }
}
