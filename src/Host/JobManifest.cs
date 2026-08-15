using System;
using System.Collections.Generic;
using System.IO;

namespace Jalyro.Convert.Host;

/// <summary>
/// The selection handed over by the shell extension.
/// Format is deliberately trivial - UTF-8, key=value header then one path
/// per line. A binary or JSON format would be premature here; the shell side
/// must stay allocation-light and fast.
/// </summary>
internal sealed class JobManifest
{
    public string Verb { get; init; } = "unknown";
    public List<string> Paths { get; init; } = new();

    public static JobManifest Load(string path)
    {
        var job = new JobManifest();
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0)
                continue;

            if (line.StartsWith("verb=", StringComparison.Ordinal))
                return new JobManifest
                {
                    Verb = line[5..],
                    Paths = ReadPaths(path)
                };
        }
        return job;
    }

    private static List<string> ReadPaths(string path)
    {
        var paths = new List<string>();
        foreach (string raw in File.ReadAllLines(path))
        {
            // Only a trailing CR is stripped. Trim() would corrupt a valid
            // filename with leading or trailing spaces - Windows permits both,
            // and the fuzz suite covers exactly that case.
            string line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            if (line.StartsWith("verb=", StringComparison.Ordinal)) continue;
            if (line.StartsWith("count=", StringComparison.Ordinal)) continue;
            paths.Add(line);
        }
        return paths;
    }
}
