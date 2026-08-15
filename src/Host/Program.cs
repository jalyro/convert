using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Jalyro.Convert.Host;

internal static class Program
{
    private const string MutexName = "Local\\Jalyro.Convert.Host.Singleton";

    private static readonly ConversionService Converter = new();
    private static Settings _settings = new();
    private static SettingsWindow? _settingsWindow;
    private static JobQueue? _queue;
    private static ProgressWindow? _progress;
    private static PipeServer? _pipeServer;
    private static SpoolWatcher? _spoolWatcher;
    private static Dispatcher? _dispatcher;
    private static readonly List<string> Activity = new();

    [STAThread]
    private static int Main(string[] args)
    {
        string installDirectory =
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";

        Storage.EnsureDirectories();

        // A directly-passed job path still works, for manual testing.
        string? directJob = null;
        foreach (string arg in args)
        {
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                directJob = arg;
                break;
            }
        }

        // Resident by DEFAULT. explorer.exe cannot carry arguments through the
        // cold-start relaunch, so the relaunched instance arrives with args=[]
        // and must still become the listener. Only an explicit job path opts out.
        bool identityOnly = HasFlag(args, "--identity");
        bool settingsOnly = HasFlag(args, "--settings");

        // --settings and --identity are one-shot windows, never the resident
        // listener.
        //
        // --identity had exactly the bug --settings just had: it became
        // resident, saw the running Host's pipe, and exited before showing
        // anything - so the diagnostics window never opened while a Host was
        // running, which is always.
        bool resident = !settingsOnly && !identityOnly
                     && (directJob is null || HasFlag(args, "--resident"));

        // ------------------------------------------------------------------
        // Cold-start identity escape.
        //
        // If the shell extension had to spawn us (no resident Host was
        // listening), we inherited package identity from the COM surrogate and
        // our writes are being virtualized into the package store.
        //
        // PROC_THREAD_ATTRIBUTE_DESKTOP_APP_POLICY does not fix this - both
        // documented values were tested in v0.2.0 and neither broke the child
        // out. So instead we relaunch ourselves VIA EXPLORER: explorer.exe is
        // unpackaged, so a process it starts has no identity. Then we exit.
        //
        // Arguments cannot be passed reliably through explorer.exe, which is
        // fine - the work is in the spool directory, not on the command line.
        // ------------------------------------------------------------------
        if (resident && Storage.HasPackageIdentity)
        {
            Storage.Log($"Cold start with package identity ({Storage.PackageFullName}).");
            Storage.Log("Relaunching via explorer.exe to shed identity, then exiting.");

            if (RelaunchViaExplorer(installDirectory))
                return 0;

            Storage.Log("Relaunch failed - continuing WITH identity. Storage will be virtualized.");
        }

        // ------------------------------------------------------------------
        // --settings: hand off to the running Host if there is one.
        //
        // Previously this lived inside "if (resident && !createdNew)", which
        // --settings never enters - so a second standalone window opened, wrote
        // the settings file, and the resident Host kept its old in-memory copy
        // until restarted.
        // ------------------------------------------------------------------
        if (settingsOnly && PipeServer.IsListenerPresent())
        {
            if (PipeServer.SendVerb("settings"))
            {
                Storage.Log("Asked the running Host to show settings.");
                return 0;
            }
            Storage.Log("Could not reach the running Host; showing settings here.");
        }

        // ------------------------------------------------------------------
        // Singleton — RESIDENT PROCESSES ONLY.
        //
        // A one-shot --settings or --identity window used to create and own
        // this mutex. If no resident Host was running, that window then blocked
        // recovery: a right-click would spawn a Host, which saw the mutex,
        // found no pipe listener, waited two seconds and exited - leaving the
        // job sitting in the spool with nothing to process it.
        // ------------------------------------------------------------------
        Mutex? singleton = null;
        bool createdNew = true;
        if (resident)
        {
            singleton = new Mutex(true, MutexName, out createdNew);
        }

        Storage.Log(new string('-', 70));
        Storage.Log($"Host starting. args=[{string.Join(" ", args)}]");
        Storage.Log($"  Package identity : {Storage.PackageFullName ?? "<none>"}");
        Storage.Log($"  Storage root     : {Storage.Root}");
        Storage.Log($"  Spool            : {Storage.SpoolDirectory}");
        Storage.Log($"  Singleton mutex  : {(createdNew ? "created (first instance)" : "already existed")}");
        Storage.Log($"  Verification file: {Storage.WriteVerificationMarker()}");

        if (resident && !createdNew)
        {
            // Another process holds the mutex - but is it actually serving?
            if (PipeServer.IsListenerPresent())
            {
                Storage.Log("Another resident Host is already listening - exiting.");
                return 0;
            }

            // No listener. Either the holder is mid-startup, or it died without
            // releasing. Try to acquire: a mutex whose owner died is ABANDONED,
            // and WaitOne throws AbandonedMutexException - which means we now
            // own it. Previously this branch just carried on WITHOUT the mutex,
            // so a third instance could start too.
            bool owned = false;
            try
            {
                owned = singleton!.WaitOne(TimeSpan.FromSeconds(2));
            }
            catch (AbandonedMutexException)
            {
                owned = true;   // previous owner died; the mutex is ours
                Storage.Log("Previous Host died without releasing the mutex - taken over.");
            }

            if (!owned)
            {
                Storage.Log("Another Host holds the mutex and is still starting - exiting.");
                return 0;
            }
        }

        _settings = Settings.Load();
        FormatTable.ApplySettings(_settings);
        ConversionService.Settings = _settings;
        Storage.Log($"Settings loaded from {Settings.Path}");

        // Both of these belong to the RESIDENT Host only.
        //
        // KillOrphanedWorkers kills Workers and ffmpeg from our install
        // directory. That is safe when a fresh resident Host has just claimed
        // the singleton - nothing legitimate is running. It is NOT safe from a
        // --settings, --identity or direct-job process: those would abort the
        // running Host's in-flight conversions. Opening the settings window
        // must not kill a conversion.
        if (resident)
        {
            SelfHeal.CheckAndRepair(installDirectory);
            KillOrphanedWorkers(installDirectory);
        }

        // A resident Host outlives every window it shows. With
        // OnMainWindowClose it would exit the moment a progress window was
        // dismissed, and the next right-click would cold-start it again.
        var app = new Application
        {
            ShutdownMode = resident
                ? ShutdownMode.OnExplicitShutdown
                : ShutdownMode.OnMainWindowClose
        };
        _dispatcher = app.Dispatcher;

        // Resident and headless by default. The diagnostics window is only
        // shown when asked for, so login does not greet the user with a dialog.
        Window? window = settingsOnly
            ? new SettingsWindow(_settings, ApplySettings)
            : (!resident || identityOnly)
                ? BuildWindow(createdNew, installDirectory, resident, identityOnly)
                : null;

        if (resident)
        {
            _queue = new JobQueue(Converter);
            _queue.Changed += OnJobChanged;
            _queue.Finished += OnJobFinished;
            _queue.FileProgress += OnFileProgress;

            // Explicit lambda: pipe input is NEVER trusted outside the spool.
            _pipeServer = new PipeServer(path => OnJobArrived(path, allowOutsideSpool: false));
            _pipeServer.Start();

            _spoolWatcher = new SpoolWatcher(path => OnJobArrived(path, allowOutsideSpool: false));
            _spoolWatcher.Start();

            app.Exit += (_, _) =>
            {
                _pipeServer?.Dispose();
                _spoolWatcher?.Dispose();
                _queue?.Dispose();
                // Closing the job object kills anything still running in it.
                Converter.Dispose();
            };
        }

        if (directJob is not null && File.Exists(directJob))
        {

            if (_queue is null)
            {
                // Without the queue the job would be claimed, deleted, and
                // silently discarded. Build one for this single run.
                _queue = new JobQueue(Converter);
                _queue.Changed += OnJobChanged;
                _queue.Finished += OnJobFinished;
                _queue.FileProgress += OnFileProgress;
                app.Exit += (_, _) => _queue?.Dispose();
            }
            OnJobArrived(directJob, allowOutsideSpool: true);
        }

        try
        {
            return window is not null ? app.Run(window) : app.Run();
        }
        finally
        {
            singleton?.Dispose();
        }
    }

    /// <summary>
    /// Kills Worker and ffmpeg processes left behind by a Host that died
    /// mid-conversion.
    ///
    /// Found in testing: two orphaned ffmpeg processes, one with 94 CPU-minutes
    /// accumulated, still encoding for a job whose Host was long gone. A user
    /// would experience that as their machine mysteriously running hot.
    ///
    /// Only safe when a fresh RESIDENT Host has just claimed the singleton, so
    /// nothing legitimate is running. Never call it from a one-shot process.
    /// </summary>
    private static void KillOrphanedWorkers(string installDirectory)
    {
        // Match on the executable PATH, not the process name.
        //
        // Killing everything called "ffmpeg.exe" would take down the user's own
        // encode running in a terminal, or another application that happens to
        // ship ffmpeg. Only processes running from our install directory are
        // ours to clean up.
        string prefix = installDirectory.TrimEnd('\\') + "\\";

        foreach (string name in new[] { "Jalyro.Convert.Worker", "ffmpeg" })
        {
            try
            {
                foreach (System.Diagnostics.Process p in
                         System.Diagnostics.Process.GetProcessesByName(name))
                {
                    using (p)
                    {
                        try
                        {
                            string? path = p.MainModule?.FileName;
                            if (path is null ||
                                !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;   // someone else's process
                            }

                            p.Kill(entireProcessTree: true);
                            Storage.Log($"Killed orphaned {name} (pid {p.Id})");
                        }
                        catch { /* access denied on another user's process, or already gone */ }
                    }
                }
            }
            catch { /* enumeration can fail; never block startup */ }
        }
    }

    private static bool HasFlag(string[] args, string flag)
    {
        return Array.Exists(args, a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Starts a clean copy of ourselves through Explorer, which is unpackaged,
    /// so the new process carries no package identity.
    /// </summary>
    private static bool RelaunchViaExplorer(string installDirectory)
    {
        try
        {
            string exe = Path.Combine(installDirectory, "Jalyro.Convert.Host.exe");

            var psi = new ProcessStartInfo
            {
                FileName = "explorer.exe",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            // ArgumentList, never a concatenated string.
            psi.ArgumentList.Add(exe);

            using Process? p = Process.Start(psi);
            return p is not null;
        }
        catch (Exception ex)
        {
            Storage.Log($"RelaunchViaExplorer: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    // -----------------------------------------------------------------------
    // Job intake
    // -----------------------------------------------------------------------
    /// <summary>
    /// A job path given on the command line is a deliberate act by whoever ran
    /// it; pipe and watcher input is not, and is confined to the spool.
    ///
    /// This was a static flag, which is a race: with a direct job AND
    /// --resident, an incoming pipe job could consume the one-shot bypass -
    /// rejecting the intended file and accepting an arbitrary path instead.
    /// Trust travels with the call.
    /// </summary>
    private static void OnJobArrived(string jobPath, bool allowOutsideSpool = false)
    {
        string? claimedPath = null;
        try
        {
            // A bare verb rather than a job path - used by --settings so the
            // Start Menu shortcut reaches the running instance.
            if (jobPath.StartsWith("VERB ", StringComparison.Ordinal))
            {
                string verb = jobPath[5..].Trim();
                if (verb.Equals("settings", StringComparison.OrdinalIgnoreCase))
                    _dispatcher?.BeginInvoke(new Action(ShowSettings));
                return;
            }

            // The pipe accepts a path from any local process running as this
            // user. Without validation the Host is a rename-and-delete proxy
            // for anything that account can reach.
            if (!allowOutsideSpool && !IsAcceptableJobPath(jobPath))
            {
                Storage.Log($"Rejected job path (not a *.job inside the spool): {jobPath}");
                return;
            }

            if (!File.Exists(jobPath))
                return;

            // The shell delivers each job TWICE: the .tmp -> .job rename fires
            // SpoolWatcher, and the pipe carries the same path. Both callbacks
            // land here, and both could pass File.Exists and load the manifest
            // before either deleted it - converting everything twice, with the
            // second run landing as "photo (1).jpg".
            //
            // Claim the file by renaming it. Exactly one caller can win an
            // atomic rename; the loser finds the file gone and returns.
            string claimed = jobPath + ".claimed";
            try
            {
                File.Move(jobPath, claimed, overwrite: false);
                claimedPath = claimed;
            }
            catch (IOException)
            {
                return;   // someone else claimed it first
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }

            jobPath = claimed;

            JobManifest job = JobManifest.Load(jobPath);

            // "Settings" is a menu entry, not a conversion. It arrives through
            // the same pipe because that is the only channel the shell has.
            if (job.Verb.Equals("settings", StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(jobPath); } catch { }
                _dispatcher?.BeginInvoke(new Action(ShowSettings));
                return;
            }
            Storage.Log($"Job: verb={job.Verb} items={job.Paths.Count} from {jobPath}");

            // Consume the job file before queuing. If the Host dies mid-run the
            // job is lost rather than replayed - a half-finished retry would be
            // worse than a clear failure.
            try { File.Delete(jobPath); } catch { /* best effort */ }

            _queue?.Enqueue(job);
        }
        catch (Exception ex)
        {
            Storage.Log($"OnJobArrived: {ex.GetType().Name}: {ex.Message}");

            // A claimed file left behind is invisible to startup recovery,
            // which only scans *.job. Remove it rather than stranding it.
            if (claimedPath is not null)
            {
                try { File.Delete(claimedPath); } catch { }
            }
        }
    }

    /// <summary>
    /// A job path must be a .job file directly inside the spool directory,
    /// after canonicalisation, and must not be a reparse point.
    /// </summary>
    private static bool IsAcceptableJobPath(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);

            if (!full.EndsWith(".job", StringComparison.OrdinalIgnoreCase))
                return false;

            string? dir = Path.GetDirectoryName(full);
            if (dir is null)
                return false;

            string spool = Path.GetFullPath(Storage.SpoolDirectory)
                               .TrimEnd(Path.DirectorySeparatorChar);

            if (!string.Equals(dir.TrimEnd(Path.DirectorySeparatorChar), spool,
                               StringComparison.OrdinalIgnoreCase))
                return false;

            var info = new FileInfo(full);
            if (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return false;

            return true;
        }
        catch
        {
            return false;
        }
    }

    // -----------------------------------------------------------------------
    // Queue events. Both arrive on a worker thread and must be marshalled.
    // -----------------------------------------------------------------------
    /// <summary>
    /// Audio and video go through ffmpeg and take seconds to minutes. Images
    /// go through libvips and typically finish in under 100 ms.
    /// </summary>
    private static bool IsLongRunning(string verb)
    {
        if (!FormatTable.TryResolve(verb, out FormatTable.Target target))
            return false;

        return target.Extension is ".mp4" or ".webm" or ".mp3" or ".wav"
                                or ".flac" or ".m4a" or ".opus";
    }

    private static void OnJobChanged(JobQueue.QueuedJob job)
    {
        // Enqueue raises Changed immediately, even while another job runs. Only
        // the job actually executing may own the window - otherwise a queued
        // batch could overwrite the running one's display, and Cancel would
        // stop a different job from the one on screen.
        if (_queue is not null && !ReferenceEquals(_queue.Current, job))
            return;

        _dispatcher?.BeginInvoke(new Action(() =>
        {
            // Recheck on the UI thread: the running job can change between the
            // test above and this callback executing.
            if (_queue is not null && !ReferenceEquals(_queue.Current, job))
                return;

            // Silence is only right for work that finishes before a window
            // could usefully appear. A single image qualifies; a single video
            // does not - a 3.5-minute re-encode with no progress and no cancel
            // button is the worst behaviour in the product.
            //
            // The rule is about expected DURATION, not file count.
            if (job.Total <= 1
                && !IsLongRunning(job.Manifest.Verb)
                && !_settings.AlwaysShowProgress)
            {
                return;
            }

            // A window that has already reported completion must not be reused:
            // its two-second auto-close timer is still pending and would close
            // during the NEW job, and its button still reads "Close".
            if (_progress is not null && _progress.IsFinished)
            {
                _progress.Close();
                _progress = null;
            }

            if (_progress is null)
            {
                _progress = new ProgressWindow(_queue!, job, IsLongRunning(job.Manifest.Verb));
                _progress.Closed += (_, _) => _progress = null;
                ForegroundHelper.Bring(_progress);
            }
            else
            {
                _progress.Update(job);
            }
        }));
    }

    private static void ShowSettings()
    {
        if (_settingsWindow is not null)
        {
            ForegroundHelper.Bring(_settingsWindow);
            return;
        }

        _settingsWindow = new SettingsWindow(_settings, ApplySettings);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        ForegroundHelper.Bring(_settingsWindow);
    }

    /// <summary>
    /// Re-applies saved settings without a restart. The format table is the
    /// only thing that reads them, so refreshing it is enough.
    /// </summary>
    private static void ApplySettings(Settings s)
    {
        _settings = s;
        FormatTable.ApplySettings(s);
        ConversionService.Settings = s;
        Storage.Log("Settings saved and applied.");
    }

    private static void OnFileProgress(int percent)
    {
        _dispatcher?.BeginInvoke(new Action(() => _progress?.UpdateFileProgress(percent)));
    }

    private static void OnJobFinished(JobQueue.QueuedJob job, ConversionService.JobOutcome outcome)
    {
        var stopwatchNote = $"{outcome.SucceededCount} converted, {outcome.FailedCount} failed";
        Storage.Log($"Job complete: {stopwatchNote}");

        foreach (ConversionService.FileOutcome f in outcome.Files)
        {
            Storage.Log(f.Succeeded
                ? $"    OK   {Path.GetFileName(f.Input)} -> {Path.GetFileName(f.Output!)}"
                : $"    FAIL {Path.GetFileName(f.Input)}: {f.Error}");
        }

        lock (Activity)
        {
            Activity.Insert(0, $"{DateTime.Now:HH:mm:ss}  {job.Manifest.Verb}  {stopwatchNote}");
            foreach (ConversionService.FileOutcome f in outcome.Files)
            {
                if (!f.Succeeded)
                    Activity.Insert(1, $"          {Path.GetFileName(f.Input)}: {f.Error}");
            }
            while (Activity.Count > 50)
                Activity.RemoveAt(Activity.Count - 1);
        }

        _dispatcher?.BeginInvoke(new Action(() =>
        {
            RefreshActivity();

            if (_progress is not null)
            {
                _progress.Complete(job, outcome);
                return;
            }

            // Single-file failures still need to be visible - silence is only
            // acceptable when it worked.
            if (outcome.FailedCount > 0 && job.Total <= 1)
            {
                ConversionService.FileOutcome? failure = null;
                foreach (ConversionService.FileOutcome f in outcome.Files)
                {
                    if (!f.Succeeded) { failure = f; break; }
                }

                if (failure is not null)
                {
                    // A background process has no foreground rights, so an
                    // ownerless MessageBox opens BEHIND whatever has focus and
                    // a refusal looks like nothing happened. A transient
                    // topmost invisible owner puts it in front.
                    var owner = new Window
                    {
                        Topmost = true,
                        WindowStyle = WindowStyle.None,
                        ShowInTaskbar = false,
                        ShowActivated = false,
                        AllowsTransparency = true,
                        Opacity = 0,
                        Width = 0,
                        Height = 0
                    };
                    owner.Show();
                    try
                    {
                        MessageBox.Show(
                            owner,
                            $"{Path.GetFileName(failure.Input)}\n\n{failure.Error}",
                            "Jalyro Convert",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                    }
                    finally
                    {
                        owner.Close();
                    }
                }
            }
        }));
    }

    // -----------------------------------------------------------------------
    // UI
    // -----------------------------------------------------------------------
    private static TextBlock? _activityBlock;

    private static void RefreshActivity()
    {
        if (_activityBlock is null)
            return;

        lock (Activity)
        {
            _activityBlock.Text = Activity.Count == 0
                ? "(waiting for jobs — right-click a supported file)"
                : string.Join(Environment.NewLine, Activity);
        }
    }

    private static Window BuildWindow(
        bool createdNewMutex,
        string installDirectory,
        bool resident,
        bool identityOnly)
    {
        var panel = new StackPanel { Margin = new Thickness(20) };

        panel.Children.Add(Heading("Jalyro Convert — Host"));
        panel.Children.Add(Body(
            resident
                ? "Running as a resident listener. Right-click an image in File "
                  + "Explorer and choose Convert to."
                : "Launched directly (not resident). Start with --resident to listen " +
                  "for jobs from the context menu."));

        // -- Identity ------------------------------------------------------
        panel.Children.Add(Section("Identity"));
        panel.Children.Add(Mono(
            $"Package identity  : {Storage.PackageFullName ?? "<none>"}\n" +
            $"Storage root      : {Storage.Root}\n" +
            $"Spool             : {Storage.SpoolDirectory}\n" +
            $"Log               : {Storage.LogPath}\n" +
            $"Singleton mutex   : {(createdNewMutex ? "created (first instance)" : "already existed")}"));

        panel.Children.Add(Body(
            Storage.HasPackageIdentity
                ? "⚠ This process HAS package identity, so its writes are being " +
                  "virtualized into the package-private store. Verify from an " +
                  "unpackaged shell — do not trust the paths above."
                : "✓ No package identity. The paths above are the real ones."));

        panel.Children.Add(Body(
            "Verify externally rather than believing this window. From PowerShell:\n" +
            "  Get-ChildItem $env:USERPROFILE\\.jalyro-convert -Recurse"));

        // -- Activity ------------------------------------------------------
        panel.Children.Add(Section("Converter"));
        panel.Children.Add(Mono(
            $"Worker         : {(Converter.WorkerAvailable ? "present" : "MISSING")}\n" +
            $"Memory cap     : {(Converter.MemoryCapActive ? "active (job object)" : "unavailable")}\n" +
            $"ffmpeg         : {(Converter.FFmpegAvailable ? "present" : "MISSING — audio, video and HEIC unavailable")}\n" +
            $"Images  in     : JPG, PNG, WEBP, AVIF, HEIC, HEIF, TIFF, BMP, GIF\n" +
            $"Images  out    : JPG, PNG, WEBP, AVIF\n" +
            $"Video   in     : MP4, MOV, MKV, WEBM, AVI, M4V, WMV, FLV, MPG\n" +
            $"Video   out    : MP4, WEBM\n" +
            $"Audio   in     : MP3, WAV, FLAC, M4A, AAC, OGG, OPUS, WMA\n" +
            $"Audio   out    : MP3, WAV, FLAC, M4A, OPUS"));

        panel.Children.Add(Section("Jobs received"));
        _activityBlock = Mono("(waiting for jobs — right-click a supported file)");
        panel.Children.Add(_activityBlock);

        // -- Paths ---------------------------------------------------------
        panel.Children.Add(Section("Paths"));
        panel.Children.Add(Mono($"Install : {installDirectory}"));

        var close = new Button
        {
            Content = resident ? "Hide" : "Close",
            Width = 90,
            Height = 30,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        panel.Children.Add(close);

        var window = new Window
        {
            Title = "Jalyro Convert — Host",
            Width = 800,
            Height = 680,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = panel
            }
        };

        close.Click += (_, _) => window.Close();

        if (identityOnly)
            window.Title += " (diagnostics)";

        return window;
    }

    private static TextBlock Heading(string text) => new()
    {
        Text = text,
        FontSize = 20,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 0, 0, 8),
        TextWrapping = TextWrapping.Wrap
    };

    private static TextBlock Section(string text) => new()
    {
        Text = text,
        FontSize = 14,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 18, 0, 6)
    };

    private static TextBlock Body(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 4)
    };

    private static TextBlock Mono(string text) => new()
    {
        Text = text,
        FontFamily = new FontFamily("Consolas"),
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 2, 0, 2)
    };
}
