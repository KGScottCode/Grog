// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;

namespace Grog.Core.Format;

public static class RelativeTime
{
    public static string Span(TimeSpan t)
    {
        if (t.TotalDays >= 365) { var y = (int)(t.TotalDays / 365); return Unit(y, "year"); }
        if (t.TotalDays >= 30) { var mo = (int)(t.TotalDays / 30); return Unit(mo, "month"); }
        if (t.TotalDays >= 1) return Unit((int)t.TotalDays, "day");
        if (t.TotalHours >= 1) return Unit((int)t.TotalHours, "hour");
        return Unit(Math.Max(1, (int)t.TotalMinutes), "minute");
    }

    public static string Ago(DateTimeOffset? t, DateTimeOffset? now = null)
    {
        if (t is null) return "Never";
        var d = (now ?? DateTimeOffset.Now) - t.Value;
        return d.TotalSeconds < 60 ? "just now" : $"{Span(d)} ago";
    }

    public static string Old(DateTimeOffset t, DateTimeOffset? now = null)
    {
        var d = (now ?? DateTimeOffset.Now) - t;
        return d.TotalMinutes < 60 ? "minutes old" : $"{Span(d)} old";
    }

    private static string Unit(int n, string unit) => Plural.Of(n, unit);
}
