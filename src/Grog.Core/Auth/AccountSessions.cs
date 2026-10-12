// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Grog.Core.Api;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Storage;

namespace Grog.Core.Auth;

public sealed class AccountSessions
{
    private readonly IManifestStore _manifest;
    private readonly GrogPaths _paths;
    private readonly HttpClient _http;
    private readonly IInteractiveLoginProvider _login;
    private readonly Dictionary<string, (GogAuthService Auth, GogApiClient Api)> _byId = new();
    private readonly Dictionary<string, bool> _authKnown = new();   // per-run
    private readonly object _lock = new();

    public Action<string>? OnThrottle { get; set; }

    public AccountSessions(IManifestStore manifest, GrogPaths paths, HttpClient http,
                           IInteractiveLoginProvider? login = null)
    {
        _manifest = manifest;
        _paths = paths;
        _http = http;
        _login = login ?? new NonInteractiveLoginProvider();
    }

    public IReadOnlyList<GrogAccount> Accounts => _manifest.Current.Accounts;

    public bool IsConnected(string accountId)
    {
        try { return File.Exists(TokenPathFor(accountId)); }
        catch { return false; }
    }

    public string TokenPathFor(string accountId)
        => new AccountService(_manifest, _paths).TokenPathFor(accountId);

    public GogApiClient ApiFor(string accountId)
    {
        lock (_lock) { return Chain(accountId).Api; }
    }

    public GogAuthService AuthFor(string accountId)
    {
        lock (_lock) { return Chain(accountId).Auth; }
    }

    public HttpClient Http => _http;

    public void Adopt(string accountId, GogAuthService auth, GogApiClient api)
    {
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(api);
        lock (_lock) { _byId[accountId] = (auth, api); _authKnown.Remove(accountId); }
    }

    private (GogAuthService Auth, GogApiClient Api) Chain(string accountId)
    {
        if (_byId.TryGetValue(accountId, out var s)) return s;
        var store = new FileTokenStore(TokenPathFor(accountId));
        var auth = new GogAuthService(_http, store, _login);
        var api = new GogApiClient(_http, auth) { OnThrottle = m => OnThrottle?.Invoke(m) };
        return _byId[accountId] = (auth, api);
    }

    public string? FirstAuthenticatedOwner(GameFile file)
    {
        foreach (var acct in Accounts)
        {
            if (!file.OwnerIds.Contains(acct.Id)) continue;
            if (!IsConnected(acct.Id)) continue;
            lock (_lock) { if (_authKnown.TryGetValue(acct.Id, out var ok) && !ok) continue; }
            return acct.Id;
        }
        return null;
    }

    public string OwnerNames(GameFile file)
        => string.Join(", ", Accounts.Where(a => file.OwnerIds.Contains(a.Id))
                                     .Select(a => string.IsNullOrEmpty(a.Username) ? (a.Id.Length == 0 ? "primary" : a.Id) : a.Username));

    public void MarkAuthFailed(string accountId) { lock (_lock) _authKnown[accountId] = false; }
    public void MarkAuthOk(string accountId) { lock (_lock) _authKnown[accountId] = true; }

    public void Invalidate(string accountId)
    {
        lock (_lock) { _byId.Remove(accountId); _authKnown.Remove(accountId); }
    }
}
