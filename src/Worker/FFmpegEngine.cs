using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Jalyro.Convert.Worker;

/// <summary>
/// Audio, video, and HEIC decoding, via ffmpeg as a subprocess.
///
/// Subprocess rather than linked libraries, for four independent reasons, any
/// one of which would be sufficient:
///
///   1. Licensing. H.264 output means libx264, which is GPL, and linking it
///      would force the GPL onto this application. Executing a separate
///      ffmpeg.exe over its documented CLI keeps the works separate.
///   2. Crash isolation. A malformed MKV that segfaults a demuxer kills a
///      disposable child.
///   3. Update independence. ffmpeg CVEs land regularly; replacing the binary
///      is a file copy.
///   4. Hardware acceleration is already wired up in standard builds.
///
/// SECURITY: every argument goes through ArgumentList as a separate element.
/// A file named "-i" or "; calc.exe" is a filename here, never a switch. Input
/// paths are additionally prefixed with "file:" and the protocol whitelist is
/// pinned, so a crafted name cannot make ffmpeg reach the network.
/// </summary>
internal static class FFmpegEngine
{
    /// <summary>ffmpeg.exe sits beside the Worker.</summary>
    public static string ExecutablePath
    {
        get
        {
            string dir = AppContext.BaseDirectory;
            return Path.Combine(dir, "ffmpeg", "ffmpeg.exe");
        }
    }

    public static bool IsAvailable => File.Exists(ExecutablePath);

    /// <summary>
    /// Container and codec settings per output format.
    /// Quality is expressed the way each encoder wants it.
    /// </summary>
    private static List<string> EncoderArguments(string format, int quality, int maxEdge)
    {
        var args = new List<string>();

        switch (format)
        {
            case "mp4":
                // H.264 + AAC. CRF is inverted relative to quality: lower is
                // better. Map 1-100 onto a sane CRF band of 18-32.
                args.AddRange(new[] { "-c:v", "libx264", "-preset", "medium" });
                args.AddRange(new[] { "-crf", CrfFromQuality(quality).ToString() });
                args.AddRange(new[] { "-c:a", "aac", "-b:a", "160k" });
                // faststart moves the index to the front so the file streams.
                args.AddRange(new[] { "-movflags", "+faststart" });
                if (maxEdge > 0)
                    args.AddRange(new[] { "-vf", ScaleFilter(maxEdge) });
                break;

            case "webm":
                // libvpx-vp9 defaults to single-threaded "best" quality. On a
                // 3.5-minute clip that consumed 94 CPU-minutes and was still
                // running. deadline=good with cpu-used=2, plus row multi-
                // threading and tile columns, is the usual sane trade: still
                // good quality, roughly an order of magnitude faster.
                args.AddRange(new[] { "-c:v", "libvpx-vp9", "-b:v", "0" });
                args.AddRange(new[] { "-deadline", "good", "-cpu-used", "2" });
                args.AddRange(new[] { "-row-mt", "1", "-tile-columns", "2" });
                // VP9's CRF scale runs 0-63 and good quality lives around
                // 30-35, not the 18-32 band x264 uses. Sharing one mapping
                // asked VP9 for near-lossless and produced files LARGER than
                // the H.264 source.
                args.AddRange(new[] { "-crf", Vp9CrfFromQuality(quality).ToString() });
                args.AddRange(new[] { "-c:a", "libopus", "-b:a", "128k" });
                if (maxEdge > 0)
                    args.AddRange(new[] { "-vf", ScaleFilter(maxEdge) });
                break;

            case "mp3":
                args.AddRange(new[] { "-vn", "-c:a", "libmp3lame", "-q:a", "2" });
                break;

            case "wav":
                args.AddRange(new[] { "-vn", "-c:a", "pcm_s16le" });
                break;

            case "flac":
                args.AddRange(new[] { "-vn", "-c:a", "flac" });
                break;

            case "m4a":
                args.AddRange(new[] { "-vn", "-c:a", "aac", "-b:a", "192k" });
                break;

            case "opus":
                args.AddRange(new[] { "-vn", "-c:a", "libopus", "-b:a", "128k" });
                break;

            case "png":
                // Used only for the HEIC decode hop; lossless intermediate.
                args.AddRange(new[] { "-frames:v", "1" });
                break;

            default:
                return new List<string>();
        }

        return args;
    }

    private static int Vp9CrfFromQuality(int quality)
    {
        if (quality <= 0) quality = 70;
        int crf = 42 - (int)Math.Round((quality / 100.0) * 14.0);
        return Math.Clamp(crf, 28, 42);
    }

    /// <summary>
    /// Bounds BOTH dimensions to maxEdge, preserving aspect ratio and never
    /// upscaling.
    ///
    /// The previous filter was scale='min(N,iw)':-2, which limits width only.
    /// A portrait 2160x3840 clip came out 720x1280 for a preset promising a
    /// 720-pixel long edge - so portrait video was over four times the
    /// intended pixel count, and the size targets suffered accordingly.
    ///
    /// force_original_aspect_ratio=decrease fits the frame inside a box; the
    /// scale=trunc(...) pass forces even dimensions, which H.264 and VP9 both
    /// require.
    /// </summary>
    private static string ScaleFilter(int maxEdge) =>
        $"scale='min({maxEdge},iw)':'min({maxEdge},ih)'"
        + ":force_original_aspect_ratio=decrease"
        // max(2,...) guards a one-pixel dimension: trunc(1/2)*2 is zero, and a
        // zero-height frame fails the encode outright.
        + ",scale='max(2,trunc(iw/2)*2)':'max(2,trunc(ih/2)*2)'";

    private static int CrfFromQuality(int quality)
    {
        if (quality <= 0) quality = 70;
        int crf = 32 - (int)Math.Round((quality / 100.0) * 14.0);
        return Math.Clamp(crf, 18, 32);
    }

    /// <summary>
    /// Codecs an MP4 container can hold without re-encoding.
    /// </summary>
    private static bool Mp4CanHold(string videoCodec, string audioCodec)
    {
        bool videoOk = videoCodec.Length == 0
            || videoCodec is "h264" or "hevc" or "mpeg4" or "av1";
        bool audioOk = audioCodec.Length == 0
            || audioCodec is "aac" or "mp3" or "alac" or "ac3";
        return videoOk && audioOk;
    }

    /// <summary>
    /// MOV to MP4 is usually a container change, not a conversion: a phone
    /// .mov already holds H.264 + AAC, which is exactly what an MP4 holds.
    /// Copying the streams is bit-identical, takes seconds instead of minutes,
    /// and the output is normally slightly SMALLER because MP4 has less
    /// container overhead.
    ///
    /// Re-encoding was producing files 40% larger than the source while
    /// throwing away quality - the worst of both.
    /// </summary>
    private static int TryStreamCopy(string input, string outputFinal, MediaInfo info)
    {
        string? outputDir = Path.GetDirectoryName(outputFinal);
        if (outputDir is null)
            return ExitCode.OutputWriteFailed;

        string temp = Path.Combine(outputDir, Result.TempPrefix + ".mp4");

        var psi = new ProcessStartInfo
        {
            FileName = ExecutablePath,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("-nostdin");
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-protocol_whitelist");
        psi.ArgumentList.Add("file");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add("file:" + input);
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("copy");
        psi.ArgumentList.Add("-movflags");
        psi.ArgumentList.Add("+faststart");
        psi.ArgumentList.Add("-sn");
        psi.ArgumentList.Add("-dn");
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("file:" + temp);

        try
        {
            using var process = new Process { StartInfo = psi };
            process.Start();

            // Drain on background threads. ReadToEnd() BEFORE WaitForExit()
            // blocks until the child closes its handles - so a hung ffmpeg
            // never reaches the timeout at all, and the timeout is decorative.
            Task<string> err = process.StandardError.ReadToEndAsync();
            Task<string> outp = process.StandardOutput.ReadToEndAsync();

            // Stream copy is I/O bound; ten minutes is generous even for a
            // very large file, and it never re-encodes.
            if (!process.WaitForExit(10 * 60 * 1000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                Result.TryDelete(temp);
                return ExitCode.EncodeFailed;
            }

            Task.WaitAll(new Task[] { err, outp }, 5000);

            if (process.ExitCode != 0 || !File.Exists(temp) || new FileInfo(temp).Length == 0)
            {
                Result.TryDelete(temp);
                return ExitCode.EncodeFailed;
            }

            return Result.Publish(temp, outputFinal, input);
        }
        catch
        {
            Result.TryDelete(temp);
            return ExitCode.EncodeFailed;
        }
    }

    public static int Convert(
        string input,
        string outputFinal,
        string format,
        int quality,
        int maxEdge,
        long targetBytes = 0,
        bool allowStreamCopy = true,
        bool propagateMotw = true)
    {
        Result.MotwEnabled = propagateMotw;

        if (!IsAvailable)
            return Result.Fail(ExitCode.UnsupportedFormat,
                "the media converter component is not installed");

        MediaInfo? info = MediaProbe.Probe(input);

        // Lossless container change where the codecs already fit.
        if (allowStreamCopy && format == "mp4" && targetBytes == 0 && maxEdge == 0
            && info is not null && Mp4CanHold(info.VideoCodec, info.AudioCodec))
        {
            int copied = TryStreamCopy(input, outputFinal, info);
            if (copied == ExitCode.Success)
                return copied;
            // Fall through and re-encode if the copy failed.
        }

        // Size-targeted presets solve a bitrate rather than guessing at a CRF.
        //
        // If the duration cannot be determined the target CANNOT be honoured,
        // and falling through to CRF encoding would quietly publish a file over
        // the advertised limit. A preset named after a size must either meet it
        // or say it could not.
        if (targetBytes > 0)
        {
            if (info is null || info.DurationSeconds <= 0.5)
            {
                return Result.Fail(ExitCode.DecodeFailed,
                    "could not read the length of this file, so it cannot be fitted "
                    + $"to {targetBytes / (1024 * 1024)} MB");
            }

            return EncodeToSize(input, outputFinal, format, info, targetBytes, maxEdge);
        }

        string? outputDir = Path.GetDirectoryName(outputFinal);
        if (outputDir is null)
            return Result.Fail(ExitCode.OutputWriteFailed, "output directory could not be determined");

        List<string> encoderArgs = EncoderArguments(format, quality, maxEdge);
        if (encoderArgs.Count == 0)
            return Result.Fail(ExitCode.UnsupportedFormat, $"unsupported output format '{format}'");

        // Temp file keeps the extension so ffmpeg picks the right muxer.
        string temp = Path.Combine(outputDir, Result.TempPrefix + "." + format);

        var psi = new ProcessStartInfo
        {
            FileName = ExecutablePath,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        // -- Safety arguments, before anything attacker-influenced ------------
        psi.ArgumentList.Add("-nostdin");          // never block waiting on input
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");

        // Machine-readable progress on stdout. Parsed against the duration
        // from ffprobe so a long encode can report an actual percentage
        // instead of an indeterminate bar that admits it knows nothing.
        psi.ArgumentList.Add("-progress");
        psi.ArgumentList.Add("pipe:1");

        // Pin the protocol whitelist. Without this a crafted .m3u8 or a concat
        // list could make ffmpeg fetch a remote URL - SSRF from a right-click.
        psi.ArgumentList.Add("-protocol_whitelist");
        psi.ArgumentList.Add("file");

        // "file:" prefix means a filename beginning with a hyphen, or one that
        // looks like "http://...", can never be reinterpreted as a protocol.
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add("file:" + input);

        foreach (string a in encoderArgs)
            psi.ArgumentList.Add(a);

        // Strip anything we did not ask for: no subtitles, no data streams, no
        // attachments. Reduces both surface area and surprise.
        psi.ArgumentList.Add("-sn");
        psi.ArgumentList.Add("-dn");
        psi.ArgumentList.Add("-map_metadata");
        psi.ArgumentList.Add("-1");

        psi.ArgumentList.Add("-y");                 // temp file, safe to clobber
        psi.ArgumentList.Add("file:" + temp);

        using var process = new Process { StartInfo = psi };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return Result.Fail(ExitCode.DecodeFailed, $"could not start the media converter: {ex.Message}");
        }

        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null && stderr.Length < 8192)
                stderr.AppendLine(e.Data);
        };
        process.BeginErrorReadLine();

        // ffmpeg emits "out_time_ms=<microseconds>" repeatedly. Despite the
        // name the unit is MICROseconds, not milliseconds - a long-standing
        // wart in ffmpeg's own output.
        // The progress loop reads until ffmpeg closes stdout, so a hung child
        // would block here forever. Bound it: the loop runs on a background
        // task and WaitForExit below owns the timeout.
        double totalSeconds = info?.DurationSeconds ?? 0;
        Task progressPump = Task.Run(() =>
        {
        string? line;
        while ((line = process.StandardOutput.ReadLine()) is not null)
        {
            if (totalSeconds <= 0) continue;
            if (!line.StartsWith("out_time_ms=", StringComparison.Ordinal)) continue;

            if (long.TryParse(line.AsSpan(12), out long micros) && micros > 0)
            {
                int percent = (int)Math.Clamp(micros / 1_000_000.0 / totalSeconds * 100.0, 0, 100);
                // One line per update on stderr would pollute the error text,
                // so progress goes to stdout in a form the Host recognises.
                Console.WriteLine($"PROGRESS {percent}");
                Console.Out.Flush();
            }
        }
        });

        // Four hours, matching the Host. A ten-minute cap here made the Host's
        // limit meaningless - and a legitimate VP9 or 4K encode passes ten
        // minutes easily, which is exactly the failure that produced a
        // "timed out" dialog for a working conversion.
        if (!process.WaitForExit(milliseconds: 4 * 60 * 60 * 1000))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            Result.TryDelete(temp);
            return Result.Fail(ExitCode.DecodeFailed, "the conversion took too long and was stopped");
        }

        progressPump.Wait(5000);

        // Parameterless WaitForExit after the timed one: the documented way to
        // guarantee all redirected output events have been delivered.
        try { process.WaitForExit(); } catch { }

        if (process.ExitCode != 0)
        {
            Result.TryDelete(temp);
            string detail = stderr.ToString().Trim();
            if (detail.Length > 400) detail = detail[..400];
            return Result.Fail(ExitCode.DecodeFailed,
                detail.Length > 0 ? detail : $"conversion failed (ffmpeg exit {process.ExitCode})");
        }

        if (!File.Exists(temp) || new FileInfo(temp).Length == 0)
        {
            Result.TryDelete(temp);
            return Result.Fail(ExitCode.EncodeFailed, "the converter produced an empty file");
        }

        return Result.Publish(temp, outputFinal, input);
    }

    /// <summary>
    /// Two-pass encode aimed at a byte target.
    ///
    /// The size presets are promises: "Discord-friendly" means under 10 MB, not
    /// "settings that might land under 10 MB". Version 0.5.2 shipped fixed
    /// quality and resolution with no reference to duration, so a 30-second
    /// clip came out needlessly small and a 10-minute clip sailed past the
    /// limit - failing at the preset's one job.
    ///
    /// Solve backwards: budget = target size, minus audio, minus container
    /// overhead, divided by duration. Single-pass CRF cannot hit a size target;
    /// two-pass can.
    /// </summary>
    private static int EncodeToSize(
        string input,
        string outputFinal,
        string format,
        MediaInfo info,
        long targetBytes,
        int maxEdge)
    {
        string? outputDir = Path.GetDirectoryName(outputFinal);
        if (outputDir is null)
            return Result.Fail(ExitCode.OutputWriteFailed, "output directory could not be determined");

        const int audioKbps = 128;

        // 4% for container overhead and rate-control drift. Undershooting is
        // free; overshooting means the preset failed.
        double budgetBits = targetBytes * 8.0 * 0.96;
        double audioBits = audioKbps * 1000.0 * info.DurationSeconds;
        double videoBits = budgetBits - audioBits;

        if (videoBits <= 0)
            return Result.Fail(ExitCode.EncodeFailed,
                "this file is too long to fit the size limit");

        int videoKbps = (int)Math.Round(videoBits / info.DurationSeconds / 1000.0);

        // Below roughly 120 kbps the result is unwatchable, and clamping up to
        // it would publish a file over the limit while claiming to have met it.
        // Refuse instead of quietly missing the promise.
        const int minimumVideoKbps = 120;
        if (videoKbps < minimumVideoKbps)
        {
            return Result.Fail(ExitCode.EncodeFailed,
                $"this file is too long to fit {targetBytes / (1024 * 1024)} MB at watchable quality");
        }

        string temp = Path.Combine(outputDir, Result.TempPrefix + "." + format);
        string logPrefix = Path.Combine(Path.GetTempPath(), Result.TempDirPrefix + "-2pass");

        int result = RunTwoPass(input, temp, logPrefix, videoKbps, audioKbps, maxEdge);

        // Verify rather than assume. If rate control overshot, retry once at a
        // bitrate scaled by how far over we landed - then verify AGAIN. The
        // previous version published the retry unchecked, so a preset named
        // after a size limit could still exceed it.
        if (result == ExitCode.Success && File.Exists(temp))
        {
            long actual = new FileInfo(temp).Length;
            if (actual > targetBytes)
            {
                double ratio = (double)targetBytes / actual;
                int retryKbps = (int)Math.Round(videoKbps * ratio * 0.92);

                if (retryKbps < minimumVideoKbps)
                {
                    Result.TryDelete(temp);
                    CleanTwoPassLogs(logPrefix);
                    return Result.Fail(ExitCode.EncodeFailed,
                        $"this file cannot be reduced to {targetBytes / (1024 * 1024)} MB "
                        + "without becoming unwatchable");
                }

                Result.TryDelete(temp);
                result = RunTwoPass(input, temp, logPrefix, retryKbps, audioKbps, maxEdge);

                if (result == ExitCode.Success && File.Exists(temp)
                    && new FileInfo(temp).Length > targetBytes)
                {
                    Result.TryDelete(temp);
                    CleanTwoPassLogs(logPrefix);
                    return Result.Fail(ExitCode.EncodeFailed,
                        $"could not get this file under {targetBytes / (1024 * 1024)} MB");
                }
            }
        }

        CleanTwoPassLogs(logPrefix);

        if (result != ExitCode.Success)
        {
            Result.TryDelete(temp);
            return result;
        }

        return Result.Publish(temp, outputFinal, input);
    }

    private static int RunTwoPass(
        string input, string temp, string logPrefix,
        int videoKbps, int audioKbps, int maxEdge)
    {
        for (int pass = 1; pass <= 2; pass++)
        {
            var psi = new ProcessStartInfo
            {
                FileName = ExecutablePath,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            psi.ArgumentList.Add("-nostdin");
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-loglevel");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-protocol_whitelist");
            psi.ArgumentList.Add("file");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add("file:" + input);

            psi.ArgumentList.Add("-c:v");
            psi.ArgumentList.Add("libx264");
            psi.ArgumentList.Add("-b:v");
            psi.ArgumentList.Add($"{videoKbps}k");
            psi.ArgumentList.Add("-preset");
            psi.ArgumentList.Add("medium");

            if (maxEdge > 0)
            {
                psi.ArgumentList.Add("-vf");
                psi.ArgumentList.Add(ScaleFilter(maxEdge));
            }

            psi.ArgumentList.Add("-pass");
            psi.ArgumentList.Add(pass.ToString());
            psi.ArgumentList.Add("-passlogfile");
            psi.ArgumentList.Add(logPrefix);

            if (pass == 1)
            {
                // First pass only measures; no audio, no output file.
                psi.ArgumentList.Add("-an");
                psi.ArgumentList.Add("-f");
                psi.ArgumentList.Add("null");
                psi.ArgumentList.Add("NUL");
            }
            else
            {
                psi.ArgumentList.Add("-c:a");
                psi.ArgumentList.Add("aac");
                psi.ArgumentList.Add("-b:a");
                psi.ArgumentList.Add($"{audioKbps}k");
                psi.ArgumentList.Add("-movflags");
                psi.ArgumentList.Add("+faststart");
                psi.ArgumentList.Add("-sn");
                psi.ArgumentList.Add("-dn");
                psi.ArgumentList.Add("-map_metadata");
                psi.ArgumentList.Add("-1");
                psi.ArgumentList.Add("-y");
                psi.ArgumentList.Add("file:" + temp);
            }

            try
            {
                using var process = new Process { StartInfo = psi };
                process.Start();

                // Async drain, then wait - never the other way round.
                Task<string> errTask = process.StandardError.ReadToEndAsync();
                Task<string> outTask = process.StandardOutput.ReadToEndAsync();

                if (!process.WaitForExit(4 * 60 * 60 * 1000))
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    return Result.Fail(ExitCode.EncodeFailed, "the conversion took too long and was stopped");
                }

                Task.WaitAll(new Task[] { errTask, outTask }, 5000);
                string error = errTask.IsCompletedSuccessfully ? errTask.Result : string.Empty;

                if (process.ExitCode != 0)
                {
                    string detail = error.Trim();
                    if (detail.Length > 400) detail = detail[..400];
                    return Result.Fail(ExitCode.EncodeFailed,
                        detail.Length > 0 ? detail : $"pass {pass} failed");
                }
            }
            catch (Exception ex)
            {
                return Result.Fail(ExitCode.EncodeFailed, $"could not run the converter: {ex.Message}");
            }
        }

        return ExitCode.Success;
    }

    private static void CleanTwoPassLogs(string logPrefix)
    {
        try
        {
            string? dir = Path.GetDirectoryName(logPrefix);
            string stem = Path.GetFileName(logPrefix);
            if (dir is null) return;

            foreach (string f in Directory.GetFiles(dir, stem + "*"))
                Result.TryDelete(f);
        }
        catch { /* best effort */ }
    }

    /// <summary>
    /// Decodes a HEIC/HEIF to a temporary PNG so libvips can take it from
    /// there. Two hops, but it reuses every tested encoding path rather than
    /// duplicating quality handling in ffmpeg.
    ///
    /// Returns the temp PNG path, or null with the error already reported.
    /// </summary>
    public static string? DecodeToTempPng(string input, out int failureCode)
    {
        failureCode = ExitCode.Success;

        // Multi-frame protection applies to HEIF ONLY.
        //
        // This method also serves video-to-still ("first frame of an MP4 as a
        // JPG"), which is a supported conversion - so rejecting anything with
        // more than one frame broke ordinary video conversions. A previous
        // version did exactly that.
        //
        // Detection is by DURATION, not frame count: a still HEIC has none,
        // while an animated one does. Frame counts derived from
        // NUMBER_OF_FRAMES metadata or the number of video streams were
        // unreliable for both formats.
        string ext = Path.GetExtension(input).ToLowerInvariant();
        if (ext is ".heic" or ".heif")
        {
            MediaInfo? probe = MediaProbe.Probe(input);
            // Any duration at all. The previous 0.1 s threshold let a
            // two-frame sequence at 25 fps through - 0.08 s - and it converted
            // to a single still with no warning. A genuine still HEIF reports
            // no duration, so there is nothing for a threshold to protect
            // against.
            if (probe is not null && probe.DurationSeconds > 0)
            {
                failureCode = Result.Fail(ExitCode.UnsupportedFormat,
                    "this is an animated HEIF; only still images can be "
                    + "converted at the moment");
                return null;
            }

            // KNOWN LIMITATION: a non-animated multi-image HEIF collection has
            // no duration and is not detected here, so only its primary image
            // is converted. Detecting that needs a HEIF container parser, which
            // is not worth adding for a rare format.
        }

        if (!IsAvailable)
        {
            failureCode = Result.Fail(ExitCode.UnsupportedFormat,
                "HEIC support requires the media converter component, which is not installed");
            return null;
        }

        // In the system temp directory, not beside the output: the Host's
        // stale-file sweep only scans the output folder, so a killed Worker
        // would otherwise strand this where nothing cleans it. Windows clears
        // %TEMP% eventually; the Worker deletes it on every non-killed path.
        // Carries the shared token so the Host can clean it up after killing
        // this Worker.
        string temp = Path.Combine(Path.GetTempPath(), Result.TempDirPrefix + "-heic.png");

        var psi = new ProcessStartInfo
        {
            FileName = ExecutablePath,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("-nostdin");
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-protocol_whitelist");
        psi.ArgumentList.Add("file");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add("file:" + input);
        // -frames:v 1 takes the first frame. For a multi-image or animated
        // HEIF that silently discards the rest - and because libvips then sees
        // the single-frame PNG rather than the original container, the
        // multi-frame guard in VipsEngine never fires. Count first.
        psi.ArgumentList.Add("-frames:v");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("file:" + temp);

        try
        {
            using var process = new Process { StartInfo = psi };
            process.Start();

            Task<string> errTask = process.StandardError.ReadToEndAsync();
            Task<string> outTask = process.StandardOutput.ReadToEndAsync();

            if (!process.WaitForExit(milliseconds: 2 * 60 * 1000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                Result.TryDelete(temp);
                failureCode = Result.Fail(ExitCode.DecodeFailed, "decoding took too long and was stopped");
                return null;
            }

            Task.WaitAll(new Task[] { errTask, outTask }, 5000);
            string error = errTask.IsCompletedSuccessfully ? errTask.Result : string.Empty;

            if (process.ExitCode != 0 || !File.Exists(temp))
            {
                Result.TryDelete(temp);
                string detail = error.Trim();
                if (detail.Length > 400) detail = detail[..400];
                failureCode = Result.Fail(ExitCode.DecodeFailed,
                    detail.Length > 0 ? detail : "the HEIC image could not be decoded");
                return null;
            }

            return temp;
        }
        catch (Exception ex)
        {
            Result.TryDelete(temp);
            failureCode = Result.Fail(ExitCode.DecodeFailed, $"HEIC decoding failed: {ex.Message}");
            return null;
        }
    }
}
