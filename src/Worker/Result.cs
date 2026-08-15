using System;
using System.IO;

namespace Jalyro.Convert.Worker;

/// <summary>Shared completion helpers for both engines.</summary>
internal static class Result
{
    /// <summary>
    /// One token shared by EVERY temporary artifact this Worker creates - the
    /// output temp file, the HEIC decode intermediate, and the two-pass logs.
    ///
    /// Previously only the output temp carried the process id, so a killed
    /// Worker still stranded heic and two-pass intermediates in %TEMP% where
    /// nothing cleaned them.
    /// </summary>
    public static string Token { get; } = $"{System.Environment.ProcessId}-{System.Guid.NewGuid():N}";

    /// <summary>Prefix for temp files written beside the output.</summary>
    public static string TempPrefix { get; } = $".jalyro-convert-{Token}";

    /// <summary>Prefix for temp files written to %TEMP%.</summary>
    public static string TempDirPrefix { get; } = $"jalyro-convert-{Token}";

    /// <summary>
    /// Set from --no-motw. The setting lives in the Host, so the Worker can
    /// only learn about it through arguments.
    /// </summary>
    public static bool MotwEnabled { get; set; } = true;

    public static int Fail(int code, string message)
    {
        Console.Error.WriteLine(message);
        return code;
    }

    public static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }

    /// <summary>
    /// Atomic publish: rename the temp file into place, then carry the
    /// Mark-of-the-Web across. Never overwrites.
    /// </summary>
    public static int Publish(string temp, string outputFinal, string source)
    {
        try
        {
            File.Move(temp, outputFinal, overwrite: false);
        }
        catch (Exception ex)
        {
            TryDelete(temp);
            return Fail(ExitCode.OutputWriteFailed, $"could not write output: {ex.Message}");
        }

        PropagateMarkOfTheWeb(source, outputFinal);
        Console.WriteLine(outputFinal);
        return ExitCode.Success;
    }

    /// <summary>
    /// Copies the Zone.Identifier alternate data stream from source to output.
    ///
    /// Without this, converting a downloaded file strips its Mark-of-the-Web
    /// and launders an untrusted file into a trusted one. Almost no converter
    /// does this; it is cheap and it is correct.
    /// </summary>
    public static void PropagateMarkOfTheWeb(string source, string destination)
    {
        if (!MotwEnabled)
            return;

        try
        {
            string sourceZone = source + ":Zone.Identifier";
            if (!File.Exists(sourceZone))
                return;

            File.WriteAllText(destination + ":Zone.Identifier", File.ReadAllText(sourceZone));
        }
        catch
        {
            // Best effort. Never fail a conversion over metadata.
        }
    }
}
