using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Jalyro.Convert.Host;

/// <summary>
/// Where the Host reads and writes state.
///
/// v0.2.1 moves storage OUT of AppData entirely, to
/// %USERPROFILE%\.jalyro-convert.
///
/// Phase 1 finding: a process launched from the COM surrogate inherits package
/// identity, and MSIX filesystem virtualization then redirects its AppData
/// writes into the package-private store. The path STRING stays correct - both
/// the environment variable and SHGetKnownFolderPath return the real path -
/// because the redirection happens below the API, at the filesystem layer.
///
/// v0.2.0 tried to detect this from inside the container by writing a marker
/// and looking for it. That could never work: reads are redirected too, so the
/// process writes to the container and reads it straight back, and everything
/// looks correct from the inside. That check has been removed rather than
/// left in place lying.
///
/// What replaces it: report the identity and the exact paths, and let the
/// operator verify from outside. Honest beats clever.
/// </summary>
internal static class Storage
{
    private const int APPMODEL_ERROR_NO_PACKAGE = 15700;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(
        ref int packageFullNameLength,
        StringBuilder? packageFullName);

    /// <summary>
    /// Null when the process has no package identity. The single most useful
    /// diagnostic the Host emits.
    /// </summary>
    public static string? PackageFullName
    {
        get
        {
            int length = 0;
            int rc = GetCurrentPackageFullName(ref length, null);
            if (rc == APPMODEL_ERROR_NO_PACKAGE)
                return null;

            var buffer = new StringBuilder(length);
            rc = GetCurrentPackageFullName(ref length, buffer);
            return rc == 0 ? buffer.ToString() : null;
        }
    }

    public static bool HasPackageIdentity => PackageFullName is not null;

    /// <summary>
    /// %USERPROFILE%\.jalyro-convert - outside AppData, so outside the
    /// paths MSIX virtualizes. Reported, not assumed.
    /// </summary>
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".jalyro-convert");

    public static string SpoolDirectory => Path.Combine(Root, "spool");
    public static string LogDirectory   => Path.Combine(Root, "logs");
    public static string LogPath        => Path.Combine(LogDirectory, "host.log");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(SpoolDirectory);
        Directory.CreateDirectory(LogDirectory);
    }

    /// <summary>
    /// A path unique to this process, for external verification. Search for
    /// this filename from an unpackaged shell to see where writes really land.
    /// </summary>
    public static string WriteVerificationMarker()
    {
        EnsureDirectories();
        string marker = Path.Combine(LogDirectory, $"marker-{Environment.ProcessId}.txt");
        try
        {
            File.WriteAllText(marker, $"pid={Environment.ProcessId} at {DateTime.Now:O}");
            return marker;
        }
        catch (Exception ex)
        {
            return $"<write failed: {ex.GetType().Name}>";
        }
    }

    private static readonly object LogLock = new();

    /// <summary>Rotate past this size. Two generations are kept.</summary>
    private const long MaxLogBytes = 2 * 1024 * 1024;

    private static void RotateIfLarge()
    {
        try
        {
            var info = new FileInfo(LogPath);
            if (!info.Exists || info.Length < MaxLogBytes)
                return;

            string previous = LogPath + ".1";
            if (File.Exists(previous))
                File.Delete(previous);
            File.Move(LogPath, previous);
        }
        catch
        {
            // Never let housekeeping break logging.
        }
    }

    public static void Log(string message)
    {
        try
        {
            EnsureDirectories();
            lock (LogLock)
            {
                RotateIfLarge();
                File.AppendAllText(
                    LogPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  pid={Environment.ProcessId}  {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the Host down.
        }
    }
}
