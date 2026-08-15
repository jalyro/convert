using System;
using System.Globalization;
using System.IO;

namespace Jalyro.Convert.Host;

/// <summary>
/// Output path resolution and refusal rules.
///
/// This runs against attacker-chosen filenames - the user downloaded a file and
/// right-clicked it. Section 9 of the design document is the threat model; this
/// class implements the path-handling half of it.
/// </summary>
internal static class PathGuard
{
    /// <summary>Names Windows reserves regardless of extension.</summary>
    private static readonly string[] ReservedNames =
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public enum Refusal
    {
        None,
        AlternateDataStream,
        ReservedDeviceName,
        ReparsePoint,
        SourceEqualsDestination,
        DirectoryEscape,
        Unresolvable
    }

    /// <summary>
    /// Canonicalises the input path and rejects the shapes we will not touch.
    /// </summary>
    public static Refusal ValidateInput(string path, out string canonical)
    {
        canonical = string.Empty;

        try
        {
            canonical = Path.GetFullPath(path);
        }
        catch
        {
            return Refusal.Unresolvable;
        }

        // Alternate data streams: "photo.jpg:evil.exe". A colon may appear only
        // as the drive separator at index 1.
        int colon = canonical.IndexOf(':', 2);
        if (colon >= 0)
            return Refusal.AlternateDataStream;

        string stem = Path.GetFileNameWithoutExtension(canonical);
        foreach (string reserved in ReservedNames)
        {
            if (string.Equals(stem, reserved, StringComparison.OrdinalIgnoreCase))
                return Refusal.ReservedDeviceName;
        }

        // Never follow a link when we are about to write next to it.
        try
        {
            var info = new FileInfo(canonical);
            if (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return Refusal.ReparsePoint;
        }
        catch
        {
            return Refusal.Unresolvable;
        }

        return Refusal.None;
    }

    /// <summary>
    /// Picks the output path for a conversion: same directory, same stem, new
    /// extension. On collision appends " (1)", " (2)", ...
    ///
    /// Never overwrites anything, and never returns the source path.
    /// </summary>
    public static Refusal ResolveOutput(
        string canonicalInput,
        string targetExtension,
        out string output)
        => ResolveOutput(canonicalInput, targetExtension, null, out output);

    /// <summary>
    /// Picks the output path, skipping names that exist on disk AND names an
    /// in-flight conversion has already claimed.
    /// </summary>
    /// <param name="isReserved">
    /// Extra "already taken" test, or null. Needed because two parallel
    /// conversions can resolve the same name before either has written
    /// anything - photo.png and photo.webp both becoming photo.jpg.
    ///
    /// An earlier attempt at this created an empty placeholder file to push the
    /// next resolve along. That was worse: the first Worker then failed to
    /// publish over the placeholder, and the placeholder was never cleaned up.
    /// Nothing here touches the filesystem.
    /// </param>
    public static Refusal ResolveOutput(
        string canonicalInput,
        string targetExtension,
        Func<string, bool>? isReserved,
        out string output)
    {
        output = string.Empty;

        string? directory = Path.GetDirectoryName(canonicalInput);
        if (directory is null)
            return Refusal.Unresolvable;

        string stem = Path.GetFileNameWithoutExtension(canonicalInput);
        string ext = targetExtension.StartsWith('.') ? targetExtension : "." + targetExtension;

        bool Taken(string path) => File.Exists(path) || (isReserved?.Invoke(path) ?? false);

        string candidate = Path.Combine(directory, stem + ext);

        for (int n = 1; Taken(candidate) && n < 10000; n++)
        {
            candidate = Path.Combine(
                directory,
                string.Format(CultureInfo.InvariantCulture, "{0} ({1}){2}", stem, n, ext));
        }

        if (Taken(candidate))
            return Refusal.Unresolvable;   // 10000 collisions: something is wrong

        // Backstop only. The collision loop above has usually already renamed
        // the candidate, so this fires rarely - the primary same-format refusal
        // lives in ConversionService, before output resolution runs.
        if (string.Equals(candidate, canonicalInput, StringComparison.OrdinalIgnoreCase))
            return Refusal.SourceEqualsDestination;

        // The resolved output must still be inside the directory we intended.
        string resolvedDir = Path.GetDirectoryName(Path.GetFullPath(candidate)) ?? string.Empty;
        if (!string.Equals(resolvedDir, directory, StringComparison.OrdinalIgnoreCase))
            return Refusal.DirectoryEscape;

        output = candidate;
        return Refusal.None;
    }

    /// <summary>Whether the destination directory can be written to.</summary>
    public static bool DirectoryIsWritable(string directory)
    {
        try
        {
            string probe = Path.Combine(directory, $".jalyro-convert-write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string Explain(Refusal refusal) => refusal switch
    {
        Refusal.AlternateDataStream     => "the filename contains an alternate data stream",
        Refusal.ReservedDeviceName      => "the filename is a reserved Windows device name",
        Refusal.ReparsePoint            => "the file is a symbolic link or junction",
        Refusal.SourceEqualsDestination => "the output would overwrite the source file",
        Refusal.DirectoryEscape         => "the output path resolved outside its own folder",
        Refusal.Unresolvable            => "the path could not be resolved",
        _                               => "no problem"
    };
}
