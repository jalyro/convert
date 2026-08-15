using System;
using System.IO;
using NetVips;

namespace Jalyro.Convert.Worker;

/// <summary>
/// Still images via libvips. Fast, low memory, correct with ICC and EXIF.
///
/// Cannot decode HEIC: the LGPL NetVips.Native build ships libheif without an
/// HEVC codec (finding #11). Those route through FFmpegEngine instead.
/// </summary>
internal static class VipsEngine
{
    /// <summary>Formats that carry no prior lossy generation.</summary>
    private static bool IsLosslessSource(string extension) =>
        extension is ".png" or ".tif" or ".tiff" or ".bmp";

    /// <summary>
    /// Output formats that discard nothing, so quality resolution does not
    /// apply to them at all.
    /// </summary>
    private static bool IsLosslessTarget(string format) =>
        format is "png" or "tif" or "tiff";

    /// <summary>
    /// Decides the encode quality for a lossy target.
    ///
    /// The naive approach - one fixed number - is wrong in both directions. A
    /// heavily-compressed source encoded at 92 produces a much larger file that
    /// faithfully preserves its artifacts. A pristine source encoded at 85
    /// discards detail nobody asked to lose.
    ///
    /// So: match the source where it had a measurable quality, and use a high
    /// default where it did not.
    /// </summary>
    private static int ResolveQuality(string input, int requested, out string reason)
    {
        string ext = Path.GetExtension(input).ToLowerInvariant();

        if (IsLosslessSource(ext))
        {
            // First and only lossy step. Be generous - the user asked to
            // convert, not to compress.
            reason = "lossless source, high default";
            return Math.Max(requested, 92);
        }

        if (ext is ".jpg" or ".jpeg")
        {
            int estimated = JpegQuality.Estimate(input);
            if (estimated > 0)
            {
                // A couple of points above the source absorbs re-encode
                // rounding without meaningfully growing the file.
                //
                // Deliberately NOT Math.Max(requested, matched): raising a
                // quality-60 photo to the configured 90 cannot recover detail,
                // it only produces a larger file preserving the same artifacts.
                // The settings window used to call this a minimum, which
                // contradicted this line; the label was corrected.
                int matched = Math.Clamp(estimated + 2, 40, 98);
                reason = $"matched source (~q{estimated})";
                return matched;
            }
        }

        reason = "unknown source quality, high default";
        return Math.Max(requested, 90);
    }

    public static int Convert(
        string input,
        string outputFinal,
        string format,
        int quality,
        int maxEdge)
    {
        string? outputDir = Path.GetDirectoryName(outputFinal);
        if (outputDir is null)
            return Result.Fail(ExitCode.OutputWriteFailed, "output directory could not be determined");

        // Animated GIF/WEBP/AVIF and multi-page TIFF load only their first
        // page by default, so the output silently dropped every other frame.
        // Refuse rather than quietly discard content: preserving animation
        // needs per-format page handling on both load and save, and a
        // converter that throws away most of a file without saying so is worse
        // than one that declines.
        try
        {
            using Image probe = Image.NewFromFile(input, access: Enums.Access.Sequential);
            int pages = probe.Get("n-pages") is int n ? n : 1;
            if (pages > 1)
            {
                return Result.Fail(ExitCode.UnsupportedFormat,
                    $"this file has {pages} frames or pages; only single-frame "
                    + "images can be converted at the moment");
            }
        }
        catch
        {
            // No n-pages metadata means a single page. Carry on.
        }

        Image image;
        try
        {
            image = maxEdge > 0
                // Thumbnail shrinks on load - much cheaper than decode-then-resize -
                // and applies EXIF orientation itself.
                ? Image.Thumbnail(input, maxEdge, height: maxEdge, size: Enums.Size.Down)
                : Image.NewFromFile(input, access: Enums.Access.Sequential).Autorot();
        }
        catch (Exception ex)
        {
            return Result.Fail(ExitCode.DecodeFailed, $"could not decode the source image: {ex.Message}");
        }

        // The temp file carries the TARGET extension, not ".tmp". Savers that
        // infer the format from the filename would otherwise fail with
        // "is not a known file format". Nothing does today - every remaining
        // format calls an explicit saver - but the next one added might.
        string tempExt = format switch
        {
            "jpeg" => "jpg",
            "tif"  => "tiff",
            _      => format
        };
        string temp = Path.Combine(outputDir, Result.TempPrefix + "." + tempExt);

        try
        {
            switch (format)
            {
                case "jpg":
                case "jpeg":
                {
                    // JPEG has no alpha; without flattening libvips composites
                    // transparent pixels onto black.
                    if (image.HasAlpha())
                    {
                        Image flattened = image.Flatten(background: new double[] { 255, 255, 255 });
                        image.Dispose();
                        image = flattened;
                    }

                    int q = quality;
                    if (maxEdge > 0)
                    {
                        // A resize preset has explicitly asked for smaller.
                        Console.Error.WriteLine($"quality {q} (preset)");
                    }
                    else
                    {
                        q = ResolveQuality(input, quality, out string why);
                        Console.Error.WriteLine($"quality {q} ({why})");
                    }

                    // subsampleMode off = 4:4:4. libvips defaults to 4:2:0,
                    // which discards three quarters of the colour resolution
                    // no matter what the quality number says - a real quality
                    // loss applied silently to every JPG output.
                    image.Jpegsave(temp, q: q, interlace: true, optimizeCoding: true,
                                   subsampleMode: Enums.ForeignSubsample.Off);
                    break;
                }

                case "png":
                    // Always lossless.
                    Console.Error.WriteLine("quality lossless (PNG)");
                    image.Pngsave(temp, compression: 6);
                    break;

                case "tif":
                case "tiff":
                    // Deflate rather than none: lossless either way, and the
                    // file is a fraction of the size. Every TIFF reader written
                    // this century handles it, including the print and archival
                    // workflows that are the reason anyone asks for TIFF.
                    Console.Error.WriteLine("quality lossless (TIFF, deflate)");
                    image.Tiffsave(temp, compression: Enums.ForeignTiffCompression.Deflate);
                    break;


                case "webp":
                {
                    string srcExt = Path.GetExtension(input).ToLowerInvariant();
                    if (IsLosslessSource(srcExt) && maxEdge == 0)
                    {
                        // PNG to WEBP has no reason to discard anything - WEBP
                        // has a lossless mode and it is usually still smaller.
                        Console.Error.WriteLine("quality lossless (lossless source)");
                        image.Webpsave(temp, lossless: true);
                    }
                    else
                    {
                        int wq = ResolveQuality(input, quality, out string wwhy);
                        Console.Error.WriteLine($"quality {wq} ({wwhy})");
                        image.Webpsave(temp, q: wq);
                    }
                    break;
                }

                case "avif":
                {
                    int q = quality;
                    if (maxEdge > 0)
                    {
                        Console.Error.WriteLine($"quality {q} (preset)");
                    }
                    else
                    {
                        q = ResolveQuality(input, quality, out string awhy);
                        Console.Error.WriteLine($"quality {q} ({awhy})");
                    }
                    image.Heifsave(temp, q: q, compression: Enums.ForeignHeifCompression.Av1);
                    break;
                }

                default:
                    return Result.Fail(ExitCode.UnsupportedFormat, $"unsupported output format '{format}'");
            }
        }
        catch (Exception ex)
        {
            Result.TryDelete(temp);

            // libvips is demand-driven: pixels are only pulled when the save
            // runs, so a DECODE failure lands here at the encode call site.
            // Classifying by exception site would blame the wrong component -
            // that mistake cost three rounds of diagnosis on HEIC.
            string message = ex.Message ?? string.Empty;
            bool looksLikeDecode =
                message.Contains("has not been built in", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("Decoder plugin", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("bad seek", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("error in tile", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("unable to load", StringComparison.OrdinalIgnoreCase);

            return looksLikeDecode
                ? Result.Fail(ExitCode.DecodeFailed, $"could not decode the source image: {message}")
                : Result.Fail(ExitCode.EncodeFailed, $"could not encode {format}: {message}");
        }
        finally
        {
            image.Dispose();
        }

        return Result.Publish(temp, outputFinal, input);
    }
}
