using System;
using System.IO;
using System.Threading;

namespace Jalyro.Convert.Host;

/// <summary>
/// Watches the spool directory for *.job files.
///
/// This is the fallback that makes the design correct regardless of whether
/// the named pipe crosses the package boundary. The shell writes to *.tmp and
/// renames to *.job, so a half-written file is never observed.
///
/// On startup it also drains anything already sitting in the spool - jobs
/// dropped while no Host was running are not lost, just delayed.
/// </summary>
internal sealed class SpoolWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly Action<string> _onJobPath;

    public SpoolWatcher(Action<string> onJobPath)
    {
        _onJobPath = onJobPath;

        Storage.EnsureDirectories();

        _watcher = new FileSystemWatcher(Storage.SpoolDirectory, "*.job")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            IncludeSubdirectories = false
        };

        _watcher.Created += OnCreated;
        _watcher.Renamed += OnRenamed;
    }

    public void Start()
    {
        _watcher.EnableRaisingEvents = true;
        Storage.Log($"SpoolWatcher: watching {Storage.SpoolDirectory}");
        DrainExisting();
    }

    /// <summary>Pick up jobs left behind while no Host was running.</summary>
    public void DrainExisting()
    {
        try
        {
            string[] pending = Directory.GetFiles(Storage.SpoolDirectory, "*.job");
            if (pending.Length > 0)
                Storage.Log($"SpoolWatcher: draining {pending.Length} pending job(s)");

            foreach (string path in pending)
                _onJobPath(path);
        }
        catch (Exception ex)
        {
            Storage.Log($"SpoolWatcher: drain failed - {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void OnCreated(object sender, FileSystemEventArgs e) => Deliver(e.FullPath);

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        // A rename raises when EITHER name matches "*.job", so the Host's own
        // claim rename (X.job -> X.job.claimed) echoed back here and was
        // logged as a rejected job on every conversion. The rename this
        // handler exists for is the shell's .tmp -> .job publish.
        if (e.FullPath.EndsWith(".claimed", StringComparison.OrdinalIgnoreCase))
            return;
        Deliver(e.FullPath);
    }

    private void Deliver(string path)
    {
        // The rename is atomic, but give the filesystem a moment before the
        // reader opens the file - FileSystemWatcher can fire very eagerly.
        Thread.Sleep(20);
        _onJobPath(path);
    }

    public void Dispose()
    {
        _watcher.EnableRaisingEvents = false;
        _watcher.Created -= OnCreated;
        _watcher.Renamed -= OnRenamed;
        _watcher.Dispose();
    }
}
