// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Grog.Core.Auth;

/// <summary>Login provider for unattended runs: never opens a browser. If GOG's refresh token has
/// lapsed and a real login is required, it throws <see cref="AuthExpiredException"/> so the job ends
/// with a clear "session expired" signal rather than blocking.</summary>
public sealed class NonInteractiveLoginProvider : IInteractiveLoginProvider
{
    public Task<string> AcquireAuthorizationCodeAsync(Uri loginUrl, CancellationToken ct = default)
        => throw new AuthExpiredException(
            "GOG session has expired and needs a one-time interactive login. Run 'grogcli login' (or sign in via the app), then retry.");
}
