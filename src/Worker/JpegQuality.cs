using System;
using System.IO;

namespace Jalyro.Convert.Worker;

/// <summary>
/// Estimates what quality setting a JPEG was originally saved at, by reading
/// its quantisation tables.
///
/// Why this matters: a fixed output quality is wrong in both directions. Encode
/// a heavily-compressed meme at 92 and you spend a much larger file faithfully
/// preserving its artifacts. Encode a pristine camera photo at 85 and you throw
/// away detail nobody asked you to lose.
///
/// Matching the source does neither. A quality-60 source comes out around 60;
/// a quality-95 source comes out around 95.
///
/// libvips does not expose this, so the DQT segment is parsed directly. No new
/// dependency, and the arithmetic is the standard IJG relationship every JPEG
/// encoder uses.
/// </summary>
internal static class JpegQuality
{
    /// <summary>
    /// The IJG standard luminance quantisation table. Every mainstream encoder
    /// scales this by a quality-derived factor, so the scaling can be inverted.
    /// </summary>
    /// <summary>
    /// Maps a position in the STORED (zig-zag) sequence to its position in
    /// natural row-major order.
    ///
    /// JPEG writes quantisation tables in zig-zag order. The first version of
    /// this class compared stored[i] against StandardLuminance[i] directly,
    /// which compares unrelated coefficients - and consistently
    /// under-estimated: a quality-60 JPEG read as 54, a quality-40 as 35.
    ///
    /// The round-trip test that "verified" it was written the same way, so it
    /// encoded and decoded through identical wrong ordering and reported
    /// perfect accuracy. A test written against the implementation rather than
    /// the format proves nothing.
    /// </summary>
    private static readonly int[] ZigZagToNatural =
    {
         0,  1,  8, 16,  9,  2,  3, 10,
        17, 24, 32, 25, 18, 11,  4,  5,
        12, 19, 26, 33, 40, 48, 41, 34,
        27, 20, 13,  6,  7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36,
        29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46,
        53, 60, 61, 54, 47, 55, 62, 63
    };

    private static readonly int[] StandardLuminance =
    {
        16, 11, 10, 16,  24,  40,  51,  61,
        12, 12, 14, 19,  26,  58,  60,  55,
        14, 13, 16, 24,  40,  57,  69,  56,
        14, 17, 22, 29,  51,  87,  80,  62,
        18, 22, 37, 56,  68, 109, 103,  77,
        24, 35, 55, 64,  81, 104, 113,  92,
        49, 64, 78, 87, 103, 121, 120, 101,
        72, 92, 95, 98, 112, 100, 103,  99
    };

    /// <summary>
    /// Estimated quality 1-100, or -1 when it cannot be determined — a
    /// progressive JPEG with unusual tables, a truncated file, or simply not a
    /// JPEG. Callers must treat -1 as "use the safe default", never as a number.
    /// </summary>
    public static int Estimate(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            return EstimateFromStream(stream);
        }
        catch
        {
            return -1;
        }
    }

    private static int EstimateFromStream(Stream stream)
    {
        // JPEG starts with SOI: FF D8
        if (stream.ReadByte() != 0xFF || stream.ReadByte() != 0xD8)
            return -1;

        // Walk markers looking for DQT (FF DB). Bail out at SOS (FF DA), where
        // entropy-coded data begins and marker parsing no longer applies.
        while (true)
        {
            int b = stream.ReadByte();
            if (b < 0) return -1;
            if (b != 0xFF) continue;

            int marker;
            do
            {
                marker = stream.ReadByte();
                if (marker < 0) return -1;
            }
            while (marker == 0xFF);   // fill bytes

            if (marker == 0xD8) continue;              // SOI
            if (marker == 0xD9) return -1;             // EOI, no DQT found
            if (marker == 0xDA) return -1;             // SOS, too late
            if (marker >= 0xD0 && marker <= 0xD7) continue;  // RSTn, no length

            int hi = stream.ReadByte();
            int lo = stream.ReadByte();
            if (hi < 0 || lo < 0) return -1;

            int length = (hi << 8) | lo;
            if (length < 2) return -1;

            if (marker == 0xDB)
            {
                var payload = new byte[length - 2];
                if (stream.Read(payload, 0, payload.Length) != payload.Length)
                    return -1;

                int estimate = EstimateFromDqt(payload);
                if (estimate > 0)
                    return estimate;

                // Table 0 was not in THIS segment. Encoders may split tables
                // across several DQT segments, so keep scanning rather than
                // giving up after the first one.
                continue;
            }

            // Not the segment we want; skip it.
            if (stream.CanSeek)
                stream.Seek(length - 2, SeekOrigin.Current);
            else
                for (int i = 0; i < length - 2; i++) stream.ReadByte();
        }
    }

    private static int EstimateFromDqt(byte[] payload)
    {
        int offset = 0;

        // A DQT segment may carry several tables. Find table id 0 - the
        // luminance table - rather than assuming it comes first. Most encoders
        // write it first; the format does not require it.
        while (offset < payload.Length)
        {
            int pq = payload[offset] >> 4;     // 0 = 8-bit values, 1 = 16-bit
            int tq = payload[offset] & 0x0F;   // table identifier
            offset++;

            var stored = new int[64];

            for (int i = 0; i < 64; i++)
            {
                if (pq == 0)
                {
                    if (offset >= payload.Length) return -1;
                    stored[i] = payload[offset++];
                }
                else
                {
                    if (offset + 1 >= payload.Length) return -1;
                    stored[i] = (payload[offset] << 8) | payload[offset + 1];
                    offset += 2;
                }
            }

            if (tq != 0)
                continue;   // chrominance or another table; keep looking

            // Un-zig-zag before comparing against the natural-order reference.
            var natural = new int[64];
            for (int k = 0; k < 64; k++)
                natural[ZigZagToNatural[k]] = stored[k];

            return QualityFromTable(natural);
        }

        return -1;
    }

    /// <summary>
    /// Inverts the IJG scaling.
    ///
    /// Encoding:  scale = quality &lt; 50 ? 5000 / quality : 200 - quality * 2
    ///            table[i] = clamp((standard[i] * scale + 50) / 100, 1, 255)
    ///
    /// So scale can be recovered per entry and averaged. Entries that clamped
    /// to 1 or 255 carry no information and are skipped — including them drags
    /// the estimate badly at the extremes.
    /// </summary>
    private static int QualityFromTable(int[] table)
    {
        // At quality 100 every entry clamps to 1, so the loop below would find
        // no usable signal and give up. The all-ones case is unambiguous.
        bool allOnes = true;
        foreach (int v in table)
        {
            if (v != 1) { allOnes = false; break; }
        }
        if (allOnes)
            return 100;

        double total = 0;
        int counted = 0;

        for (int i = 0; i < 64; i++)
        {
            int value = table[i];
            if (value <= 1 || value >= 255)
                continue;

            double scale = (value * 100.0 - 50.0) / StandardLuminance[i];
            if (scale <= 0)
                continue;

            total += scale;
            counted++;
        }

        if (counted < 8)
            return -1;   // too little signal to be trustworthy

        double averageScale = total / counted;

        double quality = averageScale > 100.0
            ? 5000.0 / averageScale                 // quality < 50
            : (200.0 - averageScale) / 2.0;         // quality >= 50

        int rounded = (int)Math.Round(quality);
        return (rounded >= 1 && rounded <= 100) ? rounded : -1;
    }
}
