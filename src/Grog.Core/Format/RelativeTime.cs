// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;

namespace Grog.Core.Format;

/// <summary>
/// THE relative-time vocabulary. Four call sites each grew their own before 09-02 (the status lede,
/// the Health tiles, the Cloud Saves rows and the behind-row age) and drifted: "5m ago" beside
/// "5 min ago" beside "5 minutes". One unit table, three phrasings.
/// </summary>
public static class RelativeTime
{
    /// <summary>"4 days" / "6 hours" / "12 minutes" / "3 months" / "2 years" - the bare span, floored
    /// to whole units and never below one minute.</summary>
    public static string Span(TimeSpan t)
    {
        if (t.TotalDays >= 365) { var y = (int)(t.TotalDays / 365); return Unit(y, "year"); }
        if (t.TotalDays >= 30) { var mo = (int)(t.TotalDays / 30); return Unit(mo, "month"); }
        if (t.TotalDays >= 1) return Unit((int)t.TotalDays, "day");
        if (t.TotalHours >= 1) return Unit((int)t.TotalHours, "hour");
        return Unit(Math.Max(1, (int)t.TotalMinutes), "minute");
    }

    /// <summary>"just now" under a minute, else "<span> ago". Null reads "Never".</summary>
    public static string Ago(DateTimeOffset? t, DateTimeOffset? now = null)
    {
        if (t is null) return "Never";
        var d = (now ?? DateTimeOffset.Now) - t.Value;
        return d.TotalSeconds < 60 ? "just now" : $"{Span(d)} ago";
    }

    /// <summary>Staleness as an AGE ("4 days old"): the behind-row reads "your backup is 4 days old",
    /// which is the reason to act rather than another date to compare.</summary>
    public static string Old(DateTimeOffset t, DateTimeOffset? now = null)
    {
        var d = (now ?? DateTimeOffset.Now) - t;
        return d.TotalMinutes < 60 ? "minutes old" : $"{Span(d)} old";
    }

    private static string Unit(int n, string unit) => Plural.Of(n, unit);
}
