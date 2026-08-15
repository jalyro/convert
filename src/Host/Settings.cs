using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jalyro.Convert.Host;

/// <summary>
/// User settings, stored as JSON next to the logs.
///
/// Deliberately small. Every option is a support burden and a decision the
/// user should not have had to make - the defaults are the product. These
/// exist because they are the ones people genuinely disagree about.
/// </summary>
internal sealed class Settings
{
    /// <summary>
    /// Encode quality for lossy image output FROM A LOSSLESS SOURCE (PNG,
    /// TIFF, BMP), and the fallback when a JPEG source's quality cannot be
    /// estimated.
    ///
    /// NOT a floor. A JPEG source is matched to its own quality instead —
    /// raising a quality-60 photo to 95 cannot recover detail, it only produces
    /// a larger file preserving the same artifacts. The settings window said
    /// "minimum", which contradicted the code; the wording was wrong, not the
    /// behaviour.
    /// </summary>
    public int JpegQuality { get; set; } = 90;
    public int WebpQuality { get; set; } = 90;
    public int AvifQuality { get; set; } = 80;

    /// <summary>Long-edge cap for "Compress for email" on images.</summary>
    public int EmailImageMaxEdge { get; set; } = 2048;

    /// <summary>Size ceilings for the video presets, in megabytes.</summary>
    public int EmailVideoMegabytes { get; set; } = 20;
    public int DiscordMegabytes { get; set; } = 10;

    /// <summary>
    /// Stream-copy MOV to MP4 when the codecs already fit. Off means always
    /// re-encode, which is slower and lossy but produces a uniform output.
    /// </summary>
    public bool PreferStreamCopy { get; set; } = true;

    /// <summary>Copy the Mark-of-the-Web from source to output.</summary>
    public bool PropagateMarkOfTheWeb { get; set; } = true;

    /// <summary>Show the progress window even for a single quick conversion.</summary>
    public bool AlwaysShowProgress { get; set; } = false;

    [JsonIgnore]
    public static string Path => System.IO.Path.Combine(Storage.Root, "settings.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static Settings Load()
    {
        try
        {
            if (!File.Exists(Path))
            {
                // Write the defaults out on first run. Otherwise the file does
                // not exist, nobody can find it, and the settings look broken
                // even though they work.
                var defaults = new Settings();
                defaults.Save();
                return defaults;
            }

            Settings? loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path), Options);
            return loaded ?? new Settings();
        }
        catch (Exception ex)
        {
            // A corrupt settings file must never stop the product working.
            Storage.Log($"Settings: could not load ({ex.GetType().Name}); using defaults");
            return new Settings();
        }
    }

    public bool Save()
    {
        try
        {
            Storage.EnsureDirectories();

            // Write then rename, so an interrupted save cannot leave a
            // half-written file that fails to parse next start.
            string temp = Path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, Options));
            File.Move(temp, Path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            Storage.Log($"Settings: could not save - {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Clamps anything a hand-edited file could get wrong.</summary>
    public void Validate()
    {
        JpegQuality = Math.Clamp(JpegQuality, 1, 100);
        WebpQuality = Math.Clamp(WebpQuality, 1, 100);
        AvifQuality = Math.Clamp(AvifQuality, 1, 100);
        EmailImageMaxEdge = Math.Clamp(EmailImageMaxEdge, 256, 16384);
        EmailVideoMegabytes = Math.Clamp(EmailVideoMegabytes, 1, 2048);
        DiscordMegabytes = Math.Clamp(DiscordMegabytes, 1, 2048);
    }
}
