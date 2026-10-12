// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;

namespace Grog.Core.Auth;

public sealed record AuthSession(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt, string UserId)
{
    public DateTimeOffset? ObtainedAt { get; init; }
}
