// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;

namespace Grog.Core.Auth;

public sealed record AuthSession(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt, string UserId)
{
    /// <summary>When this session was obtained or last refreshed. GOG rotates the refresh token on each
    /// exchange, so this is the freshness anchor for the unattended window. Null for tokens saved before
    /// this field existed.</summary>
    public DateTimeOffset? ObtainedAt { get; init; }
}
