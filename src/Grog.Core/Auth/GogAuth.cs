// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Grog.Core.Auth;

/// <summary>Authenticates the way GOG Galaxy does: browser login captures ?code= from the on_login_success
/// redirect, the code is exchanged at auth.gog.com/token, and ~1h access tokens are refreshed silently.
/// The Desktop project supplies the WebView via IInteractiveLoginProvider; Core stays UI-free.</summary>
public interface IAuthService
{
    bool HasStoredSession { get; }
    Task<AuthSession> EnsureAuthenticatedAsync(CancellationToken ct = default);
    Task SignOutAsync();
}

/// <summary>Implemented by the GUI (Avalonia WebView window) or CLI (system-browser + paste-code fallback).</summary>
public interface IInteractiveLoginProvider
{
    /// <returns>The authorization code captured from the on_login_success redirect.</returns>
    Task<string> AcquireAuthorizationCodeAsync(Uri loginUrl, CancellationToken ct = default);
}




/// <summary>Persists tokens. Desktop impl should use OS-appropriate protected storage
/// (DPAPI on Windows, libsecret on Linux, Keychain on macOS) with a file fallback.</summary>
public interface ITokenStore
{
    Task<AuthSession?> LoadAsync();
    Task SaveAsync(AuthSession session);
    Task ClearAsync();
}
