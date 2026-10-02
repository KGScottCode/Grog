// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;

namespace Grog.Core.Auth;

/// <summary>GOG offers no public OAuth registration; third-party clients all use GOG Galaxy's own public
/// client credentials. Users authenticate with their own account through GOG's real login page -- these
/// constants only identify the application, never the user.</summary>
public static class GogOAuth
{
    public const string ClientId = "46899977096215655";
    public const string ClientSecret = "9d85c43b1482497dbbce61f6e4aa173a433796eeae2ca8c5f6129f2dc4de46d9";
    public const string RedirectUri = "https://embed.gog.com/on_login_success?origin=client";

    public const string AuthorizeUrl =
        "https://auth.gog.com/auth?client_id=" + ClientId +
        "&redirect_uri=" + "https%3A%2F%2Fembed.gog.com%2Fon_login_success%3Forigin%3Dclient" +
        "&response_type=code&layout=client2";

    public static string TokenExchangeUrl(string code) =>
        "https://auth.gog.com/token?client_id=" + ClientId +
        "&client_secret=" + ClientSecret +
        "&grant_type=authorization_code" +
        "&code=" + Uri.EscapeDataString(code) +
        "&redirect_uri=" + Uri.EscapeDataString(RedirectUri);

    public static string TokenRefreshUrl(string refreshToken) =>
        "https://auth.gog.com/token?client_id=" + ClientId +
        "&client_secret=" + ClientSecret +
        "&grant_type=refresh_token" +
        "&refresh_token=" + Uri.EscapeDataString(refreshToken);

    /// <summary>Refresh-token grant scoped to a specific GAME's client credentials (not Galaxy's).
    /// GOG's per-game cloud-storage endpoint 403s the generic session token; it accepts a token minted
    /// with the game's own clientId/clientSecret. Mirrors gogdl's cloud-save auth.</summary>
    public static string GameTokenUrl(string gameClientId, string gameClientSecret, string refreshToken) =>
        "https://auth.gog.com/token?client_id=" + Uri.EscapeDataString(gameClientId) +
        "&client_secret=" + Uri.EscapeDataString(gameClientSecret) +
        "&grant_type=refresh_token" +
        "&refresh_token=" + Uri.EscapeDataString(refreshToken);

    /// <summary>Accepts what a user pastes after logging in -- the full on_login_success redirect URL or the
    /// bare code value. Null when no code can be found.</summary>
    public static string? ExtractCode(string pasted)
    {
        if (string.IsNullOrWhiteSpace(pasted)) return null;
        pasted = pasted.Trim();

        // Bare code: digits/letters, no URL parts
        if (!pasted.Contains("://") && !pasted.Contains('?') && !pasted.Contains('='))
            return pasted;

        var marker = "code=";
        var idx = pasted.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;

        var start = idx + marker.Length;
        var end = pasted.IndexOfAny(new[] { '&', '#', ' ' }, start);
        var code = end < 0 ? pasted[start..] : pasted[start..end];
        return string.IsNullOrWhiteSpace(code) ? null : Uri.UnescapeDataString(code);
    }
}
