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

/// <summary>Per-account session cache: one lazy FileTokenStore -> GogAuthService -> GogApiClient chain per
/// registered account, keyed by account id; any authenticated owner can serve a file. DEFAULT LOGIN PROVIDER
/// IS NON-INTERACTIVE: sync/download passes never pop a login window mid-run -- a lapsed session skips with
/// a log line; hosts wanting interactive login construct their own session for it.</summary>
public sealed class AccountSessions
{
    private readonly IManifestStore _manifest;
    private readonly GrogPaths _paths;
    private readonly HttpClient _http;
    private readonly IInteractiveLoginProvider _login;
    private readonly Dictionary<string, (GogAuthService Auth, GogApiClient Api)> _byId = new();
    private readonly Dictionary<string, bool> _authKnown = new();   // per-run "does this session authenticate"
    private readonly object _lock = new();

    /// <summary>Surfaced once per backoff wait by whichever account's client is throttled.</summary>
    public Action<string>? OnThrottle { get; set; }

    public AccountSessions(IManifestStore manifest, GrogPaths paths, HttpClient http,
                           IInteractiveLoginProvider? login = null)
    {
        _manifest = manifest;
        _paths = paths;
        _http = http;
        _login = login ?? new NonInteractiveLoginProvider();
    }

    /// <summary>Registered accounts in registration order - the same order every owners list uses.</summary>
    public IReadOnlyList<GrogAccount> Accounts => _manifest.Current.Accounts;

    /// <summary>The cheap probe: a token file exists for this account. Whether it still AUTHENTICATES is
    /// answered by using it (and cached per run via <see cref="MarkAuthFailed"/>/successful use).</summary>
    public bool IsConnected(string accountId)
    {
        try { return File.Exists(TokenPathFor(accountId)); }
        catch { return false; }
    }

    public string TokenPathFor(string accountId)
        => new AccountService(_manifest, _paths).TokenPathFor(accountId);

    /// <summary>The api for one account, built on first use. Exists whether or not the account is
    /// signed in - callers gate on <see cref="IsConnected"/> (cheap) or catch AuthExpiredException.</summary>
    public GogApiClient ApiFor(string accountId)
    {
        lock (_lock) { return Chain(accountId).Api; }
    }

    /// <summary>The auth half of an account's chain, for services built on IAuthService rather than the
    /// api client (cloud saves, changelog). Same non-interactive session the api uses.</summary>
    public GogAuthService AuthFor(string accountId)
    {
        lock (_lock) { return Chain(accountId).Auth; }
    }

    /// <summary>The shared HttpClient, so hosts can construct sibling services (CloudSaveService) on the
    /// same connection pool as the session chains.</summary>
    public HttpClient Http => _http;

    /// <summary>Reuse a host's already-built auth + api pair for an account (the App's interactive session is
    /// bound to the first account's token file) instead of building a second GogAuthService over the same
    /// store, so two instances never refresh against each other's rotated refresh token. Replaces any chain
    /// cached for that id.</summary>
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
        // Each account's client carries its own 429/5xx backoff state; the per-IP budget is shared but the
        // sync runner is sequential per account, so a shared gate is not needed yet.
        var api = new GogApiClient(_http, auth) { OnThrottle = m => OnThrottle?.Invoke(m) };
        return _byId[accountId] = (auth, api);
    }

    /// <summary>The download picker: the FIRST owner (registration order) of this file whose session is
    /// usable this run. Null when every owner is signed out - the caller skips with a log line naming
    /// the accounts; that is "no key to the door today", never an error strike and never Unavailable
    /// (GOG refused nothing).</summary>
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

    /// <summary>Names for a "every owner is signed out" log line, registration order.</summary>
    public string OwnerNames(GameFile file)
        => string.Join(", ", Accounts.Where(a => file.OwnerIds.Contains(a.Id))
                                     .Select(a => string.IsNullOrEmpty(a.Username) ? (a.Id.Length == 0 ? "primary" : a.Id) : a.Username));

    /// <summary>A 401/AuthExpired mid-run invalidates ONE account for the rest of the run (its refresh
    /// token lapsed); other owners keep serving. Cleared by <see cref="MarkAuthOk"/> after a re-login.</summary>
    public void MarkAuthFailed(string accountId) { lock (_lock) _authKnown[accountId] = false; }
    public void MarkAuthOk(string accountId) { lock (_lock) _authKnown[accountId] = true; }

    /// <summary>Forget a cached session chain (after logout/remove/re-login, so the next use rebuilds
    /// from the token files on disk instead of a stale in-memory session).</summary>
    public void Invalidate(string accountId)
    {
        lock (_lock) { _byId.Remove(accountId); _authKnown.Remove(accountId); }
    }
}
