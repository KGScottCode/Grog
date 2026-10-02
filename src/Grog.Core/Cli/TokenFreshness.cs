// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using Grog.Core.Auth;

namespace Grog.Core.Cli;

/// <summary>Human readout of stored-session freshness. Informational, not a countdown -- GOG publishes
/// no refresh-token max age. Pure (takes a clock).</summary>
public static class TokenFreshness
{
    public sealed record Readout(string LastRefreshed, string AccessToken, bool HasRefreshToken);

    public static Readout For(AuthSession s, DateTimeOffset now)
    {
        string lastRefreshed = s.ObtainedAt is { } ob
            ? $"{Human(now - ob)} ago ({ob.LocalDateTime:g})"
            : "unknown (saved before freshness tracking)";

        string access = now < s.ExpiresAt
            ? $"valid for {Human(s.ExpiresAt - now)}"
            : $"expired {Human(now - s.ExpiresAt)} ago (auto-refreshes on next use)";

        return new Readout(lastRefreshed, access, !string.IsNullOrEmpty(s.RefreshToken));
    }

    /// <summary>Compact duration: "under a minute", "42 min", "3h 5m", "6d 2h".</summary>
    public static string Human(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = t.Negate();
        if (t.TotalMinutes < 1) return "under a minute";
        if (t.TotalHours < 1) return $"{(int)t.TotalMinutes} min";
        if (t.TotalDays < 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        return $"{(int)t.TotalDays}d {t.Hours}h";
    }
}
