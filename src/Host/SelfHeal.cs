using System;
using System.Diagnostics;
using System.IO;

namespace Jalyro.Convert.Host;

/// <summary>
/// Re-registers the sparse package if the registration has gone missing.
///
/// This directly targets the incumbent's largest bug class: File Converter has
/// a long tail of "the menu appeared once then vanished / ShellExView shows the
/// extension disabled / reinstalling did not fix it" reports. Checking on every
/// Host start and silently repairing turns that from a support burden into a
/// non-event.
/// </summary>
internal static class SelfHeal
{
    private const string PackageName = "Jalyro.Convert";

    public static void CheckAndRepair(string installDirectory)
    {
        try
        {
            if (IsRegistered())
            {
                Storage.Log("SelfHeal: package registration present.");
                return;
            }

            string msix = Path.Combine(installDirectory, "Jalyro.Convert.msix");
            if (!File.Exists(msix))
            {
                Storage.Log($"SelfHeal: registration MISSING and {msix} not found - cannot repair.");
                return;
            }

            Storage.Log("SelfHeal: registration MISSING - re-registering.");
            // A path containing an apostrophe would otherwise terminate the
            // PowerShell string early. In single-quoted PowerShell, '' is a
            // literal apostrophe.
            Run($"Add-AppxPackage -Path '{PsQuote(msix)}' "
              + $"-ExternalLocation '{PsQuote(installDirectory)}'");

            Storage.Log(IsRegistered()
                ? "SelfHeal: re-registration succeeded."
                : "SelfHeal: re-registration FAILED.");
        }
        catch (Exception ex)
        {
            Storage.Log($"SelfHeal: threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool IsRegistered()
    {
        string output = Run($"if (Get-AppxPackage -Name '{PsQuote(PackageName)}') {{ 'yes' }} else {{ 'no' }}");
        return output.Contains("yes", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Escapes a value for a PowerShell single-quoted string.</summary>
    private static string PsQuote(string value) => value.Replace("'", "''");

    private static string Run(string script)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        // ArgumentList, never a concatenated command line - the same discipline
        // that will apply to every ffmpeg invocation in Phase 3.
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);

        using var process = Process.Start(psi);
        if (process is null)
            return string.Empty;

        // Async drain then wait. ReadToEnd() first would block past the timeout
        // if PowerShell hung - the same pattern that made the ffmpeg timeouts
        // decorative. The return value is also honoured now: a stuck process
        // gets killed rather than left running.
        System.Threading.Tasks.Task<string> outTask = process.StandardOutput.ReadToEndAsync();
        System.Threading.Tasks.Task<string> errTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(30_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            Storage.Log("SelfHeal: PowerShell timed out and was stopped.");
            return string.Empty;
        }

        System.Threading.Tasks.Task.WaitAll(
            new System.Threading.Tasks.Task[] { outTask, errTask }, 5000);

        return outTask.IsCompletedSuccessfully ? outTask.Result : string.Empty;
    }
}
