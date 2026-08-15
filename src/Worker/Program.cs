using System;
using System.IO;

namespace Jalyro.Convert.Worker;

/// <summary>
/// Converts exactly one file, then exits.
///
/// Usage:
///   Jalyro.Convert.Worker.exe --input &lt;path&gt; --output &lt;path&gt;
///                                --format jpg|png|webp|avif|mp4|webm|mp3|wav|flac|m4a|opus
///                                [--quality 1-100] [--max-edge px]
///
/// Routes between two engines:
///   libvips  still images, everything except HEIC
///   ffmpeg   audio, video, and HEIC decoding (finding #11)
/// </summary>
internal static class Program
{
    private static readonly string[] VipsOutputs =
        { "jpg", "jpeg", "png", "webp", "avif", "tif", "tiff" };
    private static readonly string[] MediaOutputs  = { "mp4", "webm", "mp3", "wav", "flac", "m4a", "opus" };
    private static readonly string[] MediaInputs   =
    {
        ".mp4", ".mov", ".mkv", ".webm", ".avi", ".m4v", ".wmv", ".flv", ".mpg", ".mpeg",
        ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".wma"
    };

    private static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{ex.GetType().Name}: {ex.Message}");
            return ExitCode.DecodeFailed;
        }
    }

    private static int Run(string[] args)
    {
        string? input = null, output = null, format = null;
        int quality = 85;
        int maxEdge = 0;
        long targetBytes = 0;
        bool allowStreamCopy = true;
        bool propagateMotw = true;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--input" when i + 1 < args.Length:   input  = args[++i]; break;
                case "--output" when i + 1 < args.Length:  output = args[++i]; break;
                case "--format" when i + 1 < args.Length:  format = args[++i].ToLowerInvariant(); break;
                case "--quality" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out quality))
                        return Result.Fail(ExitCode.BadArguments, "bad --quality");
                    break;
                case "--max-edge" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out maxEdge))
                        return Result.Fail(ExitCode.BadArguments, "bad --max-edge");
                    break;
                case "--no-stream-copy": allowStreamCopy = false; break;
                case "--no-motw":         propagateMotw = false; break;
                case "--target-bytes" when i + 1 < args.Length:
                    if (!long.TryParse(args[++i], out targetBytes))
                        return Result.Fail(ExitCode.BadArguments, "bad --target-bytes");
                    break;
                // Lets the test harness call the REAL estimator instead of a
                // Python reimplementation of it. A reimplemented test can pass
                // while the shipped code regresses - which is close to what
                // happened when the original round-trip test validated its own
                // wrong zig-zag ordering.
                case "--jpeg-quality" when i + 1 < args.Length:
                {
                    string probe = args[++i];
                    if (!File.Exists(probe))
                    {
                        Console.Error.WriteLine("file not found");
                        return ExitCode.InputUnreadable;
                    }
                    Console.WriteLine(JpegQuality.Estimate(probe));
                    return ExitCode.Success;
                }

                case "--version":
                    Console.WriteLine(
                        $"libvips {NetVips.NetVips.Version(0)}.{NetVips.NetVips.Version(1)}.{NetVips.NetVips.Version(2)}");
                    Console.WriteLine($"ffmpeg  {(FFmpegEngine.IsAvailable ? "present" : "MISSING")}");
                    Console.WriteLine("probe   via ffmpeg -i (ffprobe not required)");
                    return ExitCode.Success;
            }
        }

        if (input is null || output is null || format is null)
            return Result.Fail(ExitCode.BadArguments,
                "usage: --input <path> --output <path> --format <fmt> [--quality N] [--max-edge N]");

        if (quality < 1 || quality > 100) quality = 85;

        if (!File.Exists(input))
            return Result.Fail(ExitCode.InputUnreadable, "input does not exist");

        string inputFull, outputFull;
        try
        {
            inputFull = Path.GetFullPath(input);
            outputFull = Path.GetFullPath(output);
        }
        catch (Exception ex)
        {
            return Result.Fail(ExitCode.RefusedUnsafePath, $"path resolution failed: {ex.GetType().Name}");
        }

        if (string.Equals(inputFull, outputFull, StringComparison.OrdinalIgnoreCase))
            return Result.Fail(ExitCode.RefusedUnsafePath, "refusing to overwrite the source file");

        string? outputDir = Path.GetDirectoryName(outputFull);
        if (outputDir is null || !Directory.Exists(outputDir))
            return Result.Fail(ExitCode.OutputWriteFailed, "output directory does not exist");

        string inputExt = Path.GetExtension(inputFull).ToLowerInvariant();

        // -------------------------------------------------------------------
        // Routing
        // -------------------------------------------------------------------

        // Media in, media out: ffmpeg end to end.
        if (Array.IndexOf(MediaOutputs, format) >= 0)
            return FFmpegEngine.Convert(inputFull, outputFull, format, quality, maxEdge,
                                        targetBytes, allowStreamCopy, propagateMotw);

        // Still image out. HEIC needs ffmpeg to decode first, because the LGPL
        // libvips build has no HEVC codec (finding #11). Decode to a temp PNG,
        // then hand to libvips so all the tested encoding paths still apply.
        Result.MotwEnabled = propagateMotw;

        if (Array.IndexOf(VipsOutputs, format) >= 0)
        {
            if (inputExt is ".heic" or ".heif")
            {
                string? decoded = FFmpegEngine.DecodeToTempPng(inputFull, out int failureCode);
                if (decoded is null)
                    return failureCode;

                try
                {
                    // Pass the ORIGINAL input as the MOTW source: the temp PNG
                    // has no zone, and the provenance we care about is the
                    // file the user actually right-clicked.
                    int code = VipsEngine.Convert(decoded, outputFull, format, quality, maxEdge);
                    if (code == ExitCode.Success)
                        Result.PropagateMarkOfTheWeb(inputFull, outputFull);
                    return code;
                }
                finally
                {
                    Result.TryDelete(decoded);
                }
            }

            // A video frame to a still is a legitimate ask, but it is ffmpeg's job.
            if (Array.IndexOf(MediaInputs, inputExt) >= 0)
            {
                string? decoded = FFmpegEngine.DecodeToTempPng(inputFull, out int failureCode);
                if (decoded is null)
                    return failureCode;

                try
                {
                    int code = VipsEngine.Convert(decoded, outputFull, format, quality, maxEdge);
                    if (code == ExitCode.Success)
                        Result.PropagateMarkOfTheWeb(inputFull, outputFull);
                    return code;
                }
                finally
                {
                    Result.TryDelete(decoded);
                }
            }

            return VipsEngine.Convert(inputFull, outputFull, format, quality, maxEdge);
        }

        return Result.Fail(ExitCode.UnsupportedFormat, $"unsupported output format '{format}'");
    }
}
