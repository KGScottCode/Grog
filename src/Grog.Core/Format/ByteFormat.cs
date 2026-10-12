// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;

namespace Grog.Core.Format;

public static class ByteFormat
{
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

    public static string Progress(long done, long total) => $"{Size(done)} / {Size(total)}";

    public static string Rate(double bytesPerSecond) => bytesPerSecond <= 0 ? "--" : $"{Size((long)bytesPerSecond)}/s";

    public static string Duration(TimeSpan t)
    {
        if (t.TotalSeconds < 1) return "0s";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        if (t.TotalMinutes >= 1) return $"{t.Minutes}m {t.Seconds}s";
        return $"{t.Seconds}s";
    }
}
