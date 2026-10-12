// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using Grog.Core.Auth;

namespace Grog.Core.Cli;

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

    public static string Human(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = t.Negate();
        if (t.TotalMinutes < 1) return "under a minute";
        if (t.TotalHours < 1) return $"{(int)t.TotalMinutes} min";
        if (t.TotalDays < 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        return $"{(int)t.TotalDays}d {t.Hours}h";
    }
}
