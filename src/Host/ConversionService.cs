using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Jalyro.Convert.Host;

/// <summary>
/// Turns a job manifest into converted files.
///
/// One Worker process per file. That is not free — process creation costs a few
/// milliseconds — but it is what keeps a malformed image from taking down the
/// queue, and it is where the restricted token goes in Phase 6.
/// </summary>
internal sealed class ConversionService : IDisposable
{
    public sealed record FileOutcome(string Input, string? Output, string? Error)
    {
        public bool Succeeded => Error is null;
    }

    /// <summary>
    /// Percentage within the file currently being converted, 0-100, or -1 when
    /// unknown. Only ffmpeg reports this; libvips conversions finish too fast
    /// for it to matter.
    /// </summary>
    public sealed record FileProgress(string Input, int Percent);

    public sealed record JobOutcome(string Verb, IReadOnlyList<FileOutcome> Files)
    {
        public int SucceededCount
        {
            get
            {
                int n = 0;
                foreach (FileOutcome f in Files) if (f.Succeeded) n++;
                return n;
            }
        }

        public int FailedCount => Files.Count - SucceededCount;
    }

    private readonly string _workerPath;

    /// <summary>
    /// Every Worker (and therefore every ffmpeg it spawns) is assigned to this
    /// job. Kill-on-close means the Host cannot leave encoders running, even
    /// if it is terminated abruptly.
    /// </summary>
    private readonly WorkerJobObject _jobObject = new();

    public bool MemoryCapActive => _jobObject.IsAvailable;

    /// <summary>
    /// Concurrency cap. Images are CPU-bound and libvips is already threaded
    /// internally, so oversubscribing hurts. Eight is a ceiling, not a target.
    /// </summary>
    private static readonly int MaxParallel = Math.Max(1, Math.Min(Environment.ProcessorCount, 8));

    /// <summary>
    /// Output paths already handed to a running Worker.
    ///
    /// PathGuard.ResolveOutput only checks whether a file EXISTS, so two
    /// parallel conversions can resolve to the same name before either has
    /// written anything - photo.png and photo.webp both becoming photo.jpg.
    /// One then fails on the atomic rename instead of becoming "photo (1).jpg".
    /// </summary>
    private readonly HashSet<string> _reservedOutputs =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Current settings, refreshed by the Host when they are saved.
    /// </summary>
    public static Settings Settings { get; set; } = new();

    public ConversionService()
    {
        string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
        _workerPath = Path.Combine(dir, "Jalyro.Convert.Worker.exe");
    }

    public bool WorkerAvailable => File.Exists(_workerPath);

    /// <summary>ffmpeg lives beside the Worker, in an ffmpeg\ subdirectory.</summary>
    public bool FFmpegAvailable
    {
        get
        {
            string? dir = Path.GetDirectoryName(_workerPath);
            return dir is not null && File.Exists(Path.Combine(dir, "ffmpeg", "ffmpeg.exe"));
        }
    }

    /// <summary>Raised as a single file advances. Not on the UI thread.</summary>
    public event Action<FileProgress>? FileProgressChanged;

    public async Task<JobOutcome> RunAsync(
        JobManifest job,
        Action? progress,
        CancellationToken token)
    {
        var outcomes = new List<FileOutcome>(job.Paths.Count);

        if (!FormatTable.TryResolve(job.Verb, out FormatTable.Target target))
        {
            foreach (string p in job.Paths)
                outcomes.Add(new FileOutcome(p, null, $"unknown conversion '{job.Verb}'"));
            return new JobOutcome(job.Verb, outcomes);
        }

        if (!WorkerAvailable)
        {
            foreach (string p in job.Paths)
                outcomes.Add(new FileOutcome(p, null, "the converter component is missing"));
            return new JobOutcome(job.Verb, outcomes);
        }

        // Refuse audio, video and HEIC up front when ffmpeg is absent, rather
        // than spawning a Worker that reports "that output format is not
        // supported" - which is both wrong and unactionable. WEBM IS supported;
        // the component was missing.
        // HEIC and video INPUTS also need ffmpeg, even when the output is a
        // still image - libvips cannot decode HEVC. Checking only the output
        // extension let a HEIC-to-JPEG conversion past the preflight and fail
        // deep inside the Worker with a worse message.
        bool needsFFmpeg = target.Extension is ".mp4" or ".webm" or ".mp3"
                                            or ".wav" or ".flac" or ".m4a" or ".opus";
        if (!needsFFmpeg)
        {
            foreach (string p in job.Paths)
            {
                FormatTable.Kind k = FormatTable.KindOf(Path.GetExtension(p));
                string ext = Path.GetExtension(p).ToLowerInvariant();
                if (k == FormatTable.Kind.Video || k == FormatTable.Kind.Audio
                    || ext is ".heic" or ".heif")
                {
                    needsFFmpeg = true;
                    break;
                }
            }
        }
        if (needsFFmpeg && !FFmpegAvailable)
        {
            foreach (string p in job.Paths)
                outcomes.Add(new FileOutcome(p, null,
                    "audio and video conversion needs the media component, which is not installed"));
            return new JobOutcome(job.Verb, outcomes);
        }

        using var gate = new SemaphoreSlim(MaxParallel);
        var tasks = new List<Task<FileOutcome>>(job.Paths.Count);

        foreach (string path in job.Paths)
        {
            string captured = path;
            tasks.Add(Task.Run(async () =>
            {
                await gate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    FileOutcome result = await ConvertOneAsync(captured, target, token)
                        .ConfigureAwait(false);
                    progress?.Invoke();
                    return result;
                }
                finally
                {
                    gate.Release();
                }
            }, token));
        }

        for (int i = 0; i < tasks.Count; i++)
        {
            string path = job.Paths[i];
            try
            {
                outcomes.Add(await tasks[i].ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                // Every input must produce an outcome. Dropping cancelled files
                // silently let the UI report "0 converted, 0 failed", which
                // reads as though nothing happened at all.
                outcomes.Add(new FileOutcome(path, null, "cancelled"));
            }
            catch (Exception ex)
            {
                outcomes.Add(new FileOutcome(path, null, ex.Message));
            }
        }

        return new JobOutcome(job.Verb, outcomes);
    }

    private async Task<FileOutcome> ConvertOneAsync(
        string input,
        FormatTable.Target target,
        CancellationToken token)
    {
        PathGuard.Refusal refusal = PathGuard.ValidateInput(input, out string canonical);
        if (refusal != PathGuard.Refusal.None)
            return new FileOutcome(input, null, PathGuard.Explain(refusal));

        string extension = Path.GetExtension(canonical);
        FormatTable.Kind kind = FormatTable.KindOf(extension);
        if (kind == FormatTable.Kind.Unsupported)
            return new FileOutcome(input, null, $"{extension} is not a supported input format");

        // Audio in, video out makes no sense and would produce a black frame.
        if (kind == FormatTable.Kind.Audio
            && (target.Extension is ".mp4" or ".webm"))
            return new FileOutcome(input, null, "an audio file cannot be converted to video");

        // Image in, audio or video out likewise.
        if (kind == FormatTable.Kind.Image
            && target.Extension is ".mp4" or ".webm" or ".mp3" or ".wav" or ".flac" or ".m4a" or ".opus")
            return new FileOutcome(input, null, "an image cannot be converted to audio or video");

        // Same format in and out, with no transform requested, is a copy - not a
        // conversion. Refuse it.
        //
        // This has to be checked HERE, before output resolution. PathGuard's
        // source-equals-destination check runs after the collision loop has
        // already renamed photo.png to photo (1).png, so by then the paths
        // differ and the check can never fire. PNG -> PNG silently produced a
        // duplicate instead of a refusal.
        //
        // A transform makes same-format legitimate: "Compress for email" is
        // jpg -> jpg with a resize, and must still work.
        bool sameFormat = string.Equals(extension, target.Extension, StringComparison.OrdinalIgnoreCase)
            || (IsJpeg(extension) && IsJpeg(target.Extension));

        if (sameFormat && target.MaxEdge == 0)
            return new FileOutcome(input, null, "the file is already in that format");

        string output;
        lock (_reservedOutputs)
        {
            // Resolve and reserve under one lock, treating both existing files
            // and in-flight reservations as taken. No placeholder is written -
            // an earlier version created one and broke the other conversion.
            refusal = PathGuard.ResolveOutput(
                canonical, target.Extension, _reservedOutputs.Contains, out output);

            if (refusal != PathGuard.Refusal.None)
                return new FileOutcome(input, null, PathGuard.Explain(refusal));

            _reservedOutputs.Add(output);
        }

        try
        {

            string? directory = Path.GetDirectoryName(output);
            if (directory is null || !PathGuard.DirectoryIsWritable(directory))
                return new FileOutcome(input, null, "the destination folder is not writable");

            var psi = new ProcessStartInfo
            {
                FileName = _workerPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            // ArgumentList, never Arguments. A file called "-q" or "--output" is a
            // filename here, not a switch. This is the single most likely
            // vulnerability in the whole product and the discipline is cheap.
            psi.ArgumentList.Add("--input");
            psi.ArgumentList.Add(canonical);
            psi.ArgumentList.Add("--output");
            psi.ArgumentList.Add(output);
            psi.ArgumentList.Add("--format");
            psi.ArgumentList.Add(target.Extension.TrimStart('.'));

            if (target.Quality > 0)
            {
                psi.ArgumentList.Add("--quality");
                psi.ArgumentList.Add(target.Quality.ToString());
            }

            if (target.MaxEdge > 0)
            {
                psi.ArgumentList.Add("--max-edge");
                psi.ArgumentList.Add(target.MaxEdge.ToString());
            }

            if (target.TargetBytes > 0)
            {
                psi.ArgumentList.Add("--target-bytes");
                psi.ArgumentList.Add(target.TargetBytes.ToString());
            }

            // Settings live in the Host; the Worker is a separate process and can
            // only learn about them through arguments. Both of these were visible
            // in the settings window and had no effect whatsoever.
            if (!Settings.PreferStreamCopy)
                psi.ArgumentList.Add("--no-stream-copy");

            if (!Settings.PropagateMarkOfTheWeb)
                psi.ArgumentList.Add("--no-motw");

            using var process = new Process { StartInfo = psi };

            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                return new FileOutcome(input, null, $"could not start the converter: {ex.Message}");
            }

            // Assign immediately after start. There is an unavoidable window
            // between CreateProcess and assignment; it is microseconds, and the
            // alternative (CREATE_SUSPENDED plus ResumeThread) is not reachable
            // through ProcessStartInfo.
            _jobObject.Assign(process);

            Task<string> stderr = process.StandardError.ReadToEndAsync();

            // Drain stdout line by line: the Worker interleaves "PROGRESS <n>"
            // lines with the final output path.
            var stdoutLines = new List<string>();
            Task stdout = Task.Run(async () =>
            {
                string? line;
                while ((line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) is not null)
                {
                    if (line.StartsWith("PROGRESS ", StringComparison.Ordinal)
                        && int.TryParse(line.AsSpan(9), out int percent))
                    {
                        FileProgressChanged?.Invoke(new FileProgress(input, percent));
                    }
                    else if (line.Length > 0)
                    {
                        stdoutLines.Add(line);
                    }
                }
            }, CancellationToken.None);

            try
            {
                // Per-file timeout. Phase 6 replaces this with a Job Object that
                // also caps memory.
                //
                // One number for every format was wrong: a five-minute cap killed a
                // 3.5-minute VP9 encode mid-run and reported it to the user as
                // "the conversion timed out", blaming their file for our setting.
                // Images finish in milliseconds; video can legitimately run for
                // hours. Time out on stuck work, not on slow work.
                TimeSpan limit = target.Extension is ".mp4" or ".webm" or ".mp3"
                                                  or ".wav" or ".flac" or ".m4a" or ".opus"
                    ? TimeSpan.FromHours(4)
                    : TimeSpan.FromMinutes(5);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(limit);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Kill the worker rather than leaving it running detached.
                //
                // Atomic publishing protects the FINAL filename, but a killed
                // Worker never runs its cleanup, so its .jalyro-convert-* temp file stays
                // beside the original. The earlier comment claiming "nothing is
                // left behind" was wrong.
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }

                // Kill() only REQUESTS termination; the process may still hold its
                // files when deletion runs. Wait briefly for it to actually exit,
                // then clean both locations with a short retry.
                try { process.WaitForExit(5000); } catch { }

                CleanTemporariesFor(Path.GetDirectoryName(output), process.Id);

                return token.IsCancellationRequested
                    ? new FileOutcome(input, null, "cancelled")
                    : new FileOutcome(input, null, "the conversion timed out and was stopped");
            }

            string errorText = (await stderr.ConfigureAwait(false)).Trim();
            await stdout.ConfigureAwait(false);
            string outputText = stdoutLines.Count > 0 ? stdoutLines[^1].Trim() : string.Empty;

            if (process.ExitCode == 0)
                return new FileOutcome(input, outputText.Length > 0 ? outputText : output, null);

            // Any unsuccessful exit can leave temporaries: a Worker crash, a
            // Job Object memory kill, or an ffmpeg internal timeout all skip
            // the Worker's own cleanup. Cleaning only on cancellation covered
            // one case out of four.
            CleanTemporariesFor(Path.GetDirectoryName(output), process.Id);

            bool isMedia = target.Extension is ".mp4" or ".webm" or ".mp3" or ".wav"
                                            or ".flac" or ".m4a" or ".opus";
            return new FileOutcome(input, null, Describe(process.ExitCode, errorText, isMedia));
        }
        finally
        {
            lock (_reservedOutputs)
            {
                _reservedOutputs.Remove(output);
            }
        }
    }

    /// <summary>
    /// Human-readable failure. Decoder messages are technical and often
    /// alarming; they go to the log, not to the user.
    /// </summary>
    private static bool IsJpeg(string extension)
        => extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Removes every temporary artifact belonging to one killed Worker.
    ///
    /// The Worker tags all of them - the output temp, the HEIC decode
    /// intermediate, and the two-pass logs - with its process id, so cleanup is
    /// precise rather than sweeping a directory and risking a concurrent
    /// conversion's live file.
    ///
    /// Retried, because a just-killed process can hold a handle for a moment
    /// after Kill returns.
    /// </summary>
    private static void CleanTemporariesFor(string? outputDirectory, int workerPid)
    {
        var locations = new List<(string dir, string pattern)>();

        if (outputDirectory is not null && Directory.Exists(outputDirectory))
            locations.Add((outputDirectory, $".jalyro-convert-{workerPid}-*"));

        // HEIC intermediates and two-pass logs live in %TEMP%.
        locations.Add((Path.GetTempPath(), $"jalyro-convert-{workerPid}-*"));

        for (int attempt = 0; attempt < 3; attempt++)
        {
            bool anyLeft = false;

            foreach ((string dir, string pattern) in locations)
            {
                try
                {
                    foreach (string f in Directory.GetFiles(dir, pattern))
                    {
                        try { File.Delete(f); }
                        catch { anyLeft = true; }
                    }
                }
                catch { /* directory vanished or is unreadable */ }
            }

            if (!anyLeft)
                return;

            Thread.Sleep(200);
        }
    }

    public void Dispose() => _jobObject.Dispose();

    private static string Describe(int exitCode, string stderrText, bool isMedia) => exitCode switch
    {
        1 => "internal error: bad arguments to the converter",
        2 => "the file could not be read",
        // Code 3 also covers two runtime refusals; without the stderr checks
        // both showed as "output format is not supported", which is wrong and
        // undiagnosable from the dialog. Strings must match the Worker's
        // Result.Fail messages in FFmpegEngine.cs.
        3 when stderrText.Contains("animated HEIF", StringComparison.OrdinalIgnoreCase)
            => "this is an animated HEIF; only still images can be converted",
        3 when stderrText.Contains("frames or pages", StringComparison.OrdinalIgnoreCase)
            => "this file has multiple frames or pages; only single-frame "
             + "images can be converted",
        3 when stderrText.Contains("media converter component", StringComparison.OrdinalIgnoreCase)
            => "HEIC, audio and video need the media converter component, "
             + "which is not installed",
        3 => "that output format is not supported",
        // Exit code 4 covers every ffmpeg failure as well as image decode, so
        // "the image could not be decoded" was shown for video and audio
        // failures too, including timeouts.
        4 when stderrText.Contains("took too long", StringComparison.OrdinalIgnoreCase)
            => "the conversion took too long and was stopped",
        4 when isMedia
            => "the file could not be converted — it may be incomplete, corrupt, "
             + "or use a codec this build cannot read",
        4 => "the image could not be decoded — it may be incomplete, corrupt, "
           + "or use a variant this build cannot read",
        5 => isMedia
                 ? "the file could not be encoded in that format"
                 : "the image could not be encoded in that format",
        6 => "the converted file could not be written",
        7 => "refused for safety: " + (stderrText.Length > 0 ? stderrText : "unsafe path"),
        // A Job Object memory kill terminates the process rather than letting
        // it return, so the exit code is the raw termination status.
        -1073741819 => "the file needed more memory than allowed and was stopped",  // 0xC0000005
        1816        => "the file needed more memory than allowed and was stopped",  // ERROR_NOT_ENOUGH_QUOTA
        _ => $"conversion failed (code {exitCode})"
    };
}
