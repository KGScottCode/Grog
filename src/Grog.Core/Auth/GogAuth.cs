// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Grog.Core.Auth;

public interface IAuthService
{
    bool HasStoredSession { get; }
    Task<AuthSession> EnsureAuthenticatedAsync(CancellationToken ct = default);
    Task SignOutAsync();
}

public interface IInteractiveLoginProvider
{
    /// <returns>The authorization code captured from the on_login_success redirect.</returns>
    Task<string> AcquireAuthorizationCodeAsync(Uri loginUrl, CancellationToken ct = default);
}




public interface ITokenStore
{
    Task<AuthSession?> LoadAsync();
    Task SaveAsync(AuthSession session);
    Task ClearAsync();
}
