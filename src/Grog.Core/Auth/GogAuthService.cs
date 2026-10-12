// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Grog.Core.Auth;

public sealed class GogAuthService : IAuthService
{
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromMinutes(2);

    private readonly HttpClient _http;
    private readonly ITokenStore _store;
    private readonly IInteractiveLoginProvider _login;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AuthSession? _session;

    public GogAuthService(HttpClient http, ITokenStore store, IInteractiveLoginProvider login)
    {
        _http = http;
        _store = store;
        _login = login;
    }

    public bool HasStoredSession => _session is not null;

    public async Task<AuthSession> EnsureAuthenticatedAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _session ??= await _store.LoadAsync();

            if (_session is null)
            {
                // No stored session: interactive login.
                var code = await _login.AcquireAuthorizationCodeAsync(new Uri(GogOAuth.AuthorizeUrl), ct);
                return await AdoptAsync(await ExchangeAsync(GogOAuth.TokenExchangeUrl(code), ct));
            }

            if (IsExpired(_session))
            {
                // Another instance on the same store may have refreshed already: use its rotated token.
                if (await SyncFromStoreAsync() && !IsExpired(_session)) return _session;
                try
                {
                    await AdoptAsync(await ExchangeAsync(GogOAuth.TokenRefreshUrl(_session.RefreshToken), ct));
                }
                catch (HttpRequestException ex) when (IsRejection(ex))
                {
                    // Refresh token rejected (revoked/expired) -- fall back to interactive login.
                    var code = await _login.AcquireAuthorizationCodeAsync(new Uri(GogOAuth.AuthorizeUrl), ct);
                    await AdoptAsync(await ExchangeAsync(GogOAuth.TokenExchangeUrl(code), ct));
                }
            }

            return _session;
        }
        finally { _gate.Release(); }
    }

    private async Task<bool> SyncFromStoreAsync()
    {
        AuthSession? disk;
        try { disk = await _store.LoadAsync(); }
        catch { return false; }   // an unreadable file says nothing; the refresh below decides
        if (disk is null || string.IsNullOrEmpty(disk.RefreshToken) || disk.RefreshToken == _session?.RefreshToken) return false;
        _session = disk;
        return true;
    }

    private async Task<AuthSession> AdoptAsync(AuthSession fresh)
    {
        await _store.SaveAsync(fresh);
        _session = fresh;
        return fresh;
    }

    public async Task SignOutAsync()
    {
        _session = null;
        await _store.ClearAsync();
    }

    public async Task PersistSessionAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { if (_session is not null) await _store.SaveAsync(_session); }
        finally { _gate.Release(); }
    }

    public async Task<AuthSession> RefreshNowAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _session ??= await _store.LoadAsync();
            if (_session is null || string.IsNullOrEmpty(_session.RefreshToken))
            {
                var code = await _login.AcquireAuthorizationCodeAsync(new Uri(GogOAuth.AuthorizeUrl), ct);
                return await AdoptAsync(await ExchangeAsync(GogOAuth.TokenExchangeUrl(code), ct));
            }
            await SyncFromStoreAsync();   // rotate from the newest token on disk, never a stale in-memory one
            try
            {
                await AdoptAsync(await ExchangeAsync(GogOAuth.TokenRefreshUrl(_session.RefreshToken), ct));
            }
            catch (HttpRequestException ex) when (IsRejection(ex))
            {
                // Refresh token rejected (revoked / password change / aged out) -- needs a real login.
                var code = await _login.AcquireAuthorizationCodeAsync(new Uri(GogOAuth.AuthorizeUrl), ct);
                await AdoptAsync(await ExchangeAsync(GogOAuth.TokenExchangeUrl(code), ct));
            }
            return _session;
        }
        finally { _gate.Release(); }
    }

    internal static bool IsRejection(HttpRequestException ex)
        => ex.StatusCode is { } c && (int)c >= 400 && (int)c < 500;

    public static bool IsExpired(AuthSession s, DateTimeOffset? now = null)
        => (now ?? DateTimeOffset.UtcNow) >= s.ExpiresAt - ExpirySkew;

    private async Task<AuthSession> ExchangeAsync(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"GOG token endpoint returned {(int)response.StatusCode}: {Truncate(body)}", null, response.StatusCode);

        var dto = JsonSerializer.Deserialize<TokenResponse>(body)
                  ?? throw new HttpRequestException("GOG token endpoint returned empty body.");

        return new AuthSession(
            AccessToken: dto.AccessToken,
            RefreshToken: dto.RefreshToken,
            ExpiresAt: DateTimeOffset.UtcNow.AddSeconds(dto.ExpiresIn),
            UserId: dto.UserId ?? "")
        {
            ObtainedAt = DateTimeOffset.UtcNow,
        };
    }

    private static string Truncate(string s) => s.Length <= 200 ? s : s[..200] + "…";

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")] public string AccessToken { get; set; } = "";
        [JsonPropertyName("refresh_token")] public string RefreshToken { get; set; } = "";
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
        [JsonPropertyName("user_id")] public string? UserId { get; set; }
    }
}
