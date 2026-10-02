// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;

namespace Grog.Core.Format;

/// <summary>Formats raw byte and rate values for display. One place, so units read consistently
/// everywhere (S0 reference-don't-repeat, S5 units scale with 1-2 decimals).</summary>
public static class ByteFormat
{
    /// <summary>THE byte formatter (every other one forwards here). E.g. 1610612736 -> "1.50 GB",
    /// 357564416 -> "341.00 MB", 8192 -> "8.00 KB", 2 TB -> "2.000 TB". Two decimals at every scale
    /// above bytes (mixing precisions reads as a bug), THREE at TB (owner call 2026-08-29: at two the last
    /// digit only moves per ~10 GB and a creeping figure looks stuck).</summary>
    public static string Size(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double kb = bytes / 1024.0;
        if (kb < 1024) return $"{kb:F2} KB";
        double mb = kb / 1024.0;
        if (mb < 1024) return $"{mb:F2} MB";
        double gb = mb / 1024.0;
        if (gb < 1024) return $"{gb:F2} GB";
        return $"{gb / 1024.0:F3} TB";
    }

    /// <summary>The narrow "got/total" form for a fixed-width progress column, e.g. "71.5/87.0 MB", "112/512 KB":
    /// one shared unit chosen by the total, one decimal, none when the value is whole.</summary>
    public static string CompactPair(long got, long total)
    {
        (double val, string unit) Scale(long b) =>
            b >= 1L << 30 ? (b / (1024.0 * 1024 * 1024), "GB")
            : b >= 1L << 20 ? (b / (1024.0 * 1024), "MB")
            : b >= 1L << 10 ? (b / 1024.0, "KB") : (b, "B");
        var (tv, unit) = Scale(total);
        var gv = unit switch { "GB" => got / (1024.0 * 1024 * 1024), "MB" => got / (1024.0 * 1024), "KB" => got / 1024.0, _ => got };
        string Fmt(double v) => v == Math.Floor(v) ? v.ToString("0") : v.ToString("0.0");
        return $"{Fmt(gv)}/{Fmt(tv)} {unit}";
    }

    /// <summary>E.g. "have / total" -> "1.20 / 4.00 GB".</summary>
    public static string Progress(long done, long total) => $"{Size(done)} / {Size(total)}";

    /// <summary>E.g. 13000000 -> "12.4 MB/s".</summary>
    public static string Rate(double bytesPerSecond) => bytesPerSecond <= 0 ? "--" : $"{Size((long)bytesPerSecond)}/s";

    /// <summary>E.g. 4530 -> "1h 15m", 620 -> "10m 20s", 45 -> "45s".</summary>
    public static string Duration(TimeSpan t)
    {
        if (t.TotalSeconds < 1) return "0s";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        if (t.TotalMinutes >= 1) return $"{t.Minutes}m {t.Seconds}s";
        return $"{t.Seconds}s";
    }
}
