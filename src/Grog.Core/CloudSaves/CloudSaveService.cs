// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Grog.Core.Auth;

namespace Grog.Core.CloudSaves;

/// <summary>Preserves GOG cloud saves locally via GOG-native endpoints: a per-game save list
/// (cloudstorage.gog.com/v1/{userId}/{clientId}) plus per-entry downloads. Saves land as timestamped
/// localSaves under &lt;root&gt;/&lt;slug&gt;/cloud-saves/&lt;utc&gt;/ so a run never overwrites older saves.</summary>
public sealed class CloudSaveService
{
    private readonly HttpClient _http;
    private readonly IAuthService _auth;

    public CloudSaveService(HttpClient http, IAuthService auth)
    {
        _http = http;
        _auth = auth;
    }

    /// <summary>Warnings a host may surface (a skipped save entry, a refused download host). Optional.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>The save's destination under <paramref name="root"/>, or null when the server-supplied name
    /// escapes it (`../`, a rooted path, a drive letter). Nothing GOG sends may pick a path outside the folder.</summary>
    internal static string? SafeDestination(string root, string name)
    {
        var rel = name.Replace('\\', '/').TrimStart('/');
        if (rel.Length == 0) return null;
        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch { return null; }
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                       + Path.DirectorySeparatorChar;
        return full.StartsWith(rootFull, StringComparison.Ordinal) ? full : null;
    }

    /// <summary>Only gog.com hosts ever see the bearer token: a save list is server data, and a download URL
    /// pointing elsewhere would hand the game-scoped token to a stranger.</summary>
    internal static bool IsTrustedDownloadHost(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return false;
        var host = uri.Host;
        return host.Equals("gog.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".gog.com", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Resolves where one save entry lands, or null (logged) when its name or URL is not acceptable.</summary>
    private string? ResolveSaveTarget(string root, CloudSaveFile save)
    {
        if (!IsTrustedDownloadHost(save.DownloadUrl))
        {
            Log?.Invoke($"Skipped cloud save '{save.Name}': download URL is not a gog.com host.");
            return null;
        }
        var dest = SafeDestination(root, save.Name);
        if (dest is null) Log?.Invoke($"Skipped cloud save '{save.Name}': its name would escape the save folder.");
        return dest;
    }

    /// <summary>Streams one save to <paramref name="dest"/> via a `.part` sibling renamed on completion, so a
    /// failure mid-file never leaves a truncated save under its final name. False when the server refused.
    /// A body shorter or longer than the listed size (<paramref name="expectedBytes"/> > 0) is a cut connection,
    /// not a save: the .part goes and the throw discards the dated folder.</summary>
    internal async Task<bool> DownloadToAsync(string url, string token, string dest, long expectedBytes, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode) return false;
        var part = dest + ".part";
        try
        {
            long received;
            await using (var fs = File.Create(part))
            {
                await res.Content.CopyToAsync(fs, ct);
                received = fs.Length;
            }
            if (expectedBytes > 0 && received != expectedBytes)
                throw new IOException($"cloud save '{Path.GetFileName(dest)}': received {received} of {expectedBytes} bytes");
            File.Move(part, dest, overwrite: true);
        }
        catch
        {
            try { File.Delete(part); } catch { /* best-effort cleanup */ }
            throw;
        }
        return true;
    }

    /// <summary>Resolves the games that have cloud saves, each with its clientId. The master-list JSON
    /// endpoint is not yet wired; callers pass known (game, clientId) pairs until it is.</summary>
    public Task<IReadOnlyList<CloudGame>> ResolveGamesWithSavesAsync(
        IReadOnlyList<CloudGame> knownPairs, CancellationToken ct = default)
    {
        // TODO: replace with a GET to the master-list endpoint once captured; map results to CloudGame.
        return Task.FromResult(knownPairs);
    }

    /// <summary>Lists one game's save files (GET cloudstorage.gog.com/v1/{userId}/{clientId}). Requires a
    /// game-scoped token from <see cref="AuthorizeGameAsync"/> -- the endpoint 403s the plain session token.
    /// 404 = the game has no cloud saves (normal).</summary>
    public async Task<IReadOnlyList<CloudSaveFile>> ListSavesAsync(string clientId, string accessToken, CancellationToken ct = default)
    {
        var session = await _auth.EnsureAuthenticatedAsync(ct);
        var baseUrl = $"https://cloudstorage.gog.com/v1/{session.UserId}/{clientId}";
        using var req = new HttpRequestMessage(HttpMethod.Get, baseUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Headers.Accept.ParseAdd("application/json");
        using var res = await _http.SendAsync(req, ct);
        // 404 is the one "no saves" answer; a 500 or a 403 read as empty would record a capture of nothing.
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound) return Array.Empty<CloudSaveFile>();
        res.EnsureSuccessStatusCode();

        var json = await res.Content.ReadAsStringAsync(ct);
        return ParseSaveList(json, baseUrl);
    }

    /// <summary>Resolves a game's clientId + clientSecret from generation-2 build meta, then mints a token
    /// scoped to them via a refresh-token grant -- the token the per-game endpoint accepts. Null on failure.</summary>
    public async Task<GameCloudAuth?> AuthorizeGameAsync(long gogId, string platform = "windows", CancellationToken ct = default)
    {
        var creds = await ResolveClientCredsAsync(gogId, platform, ct);
        if (creds is null) return null;
        var session = await _auth.EnsureAuthenticatedAsync(ct);
        if (string.IsNullOrEmpty(session.RefreshToken)) return null;
        try
        {
            using var res = await _http.GetAsync(
                GogOAuth.GameTokenUrl(creds.Value.ClientId, creds.Value.ClientSecret, session.RefreshToken), ct);
            if (!res.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            var token = doc.RootElement.TryGetProperty("access_token", out var t) ? t.GetString() : null;
            return string.IsNullOrEmpty(token) ? null : new GameCloudAuth(creds.Value.ClientId, token!);
        }
        catch { return null; }
    }

    /// <summary>ClientId + clientSecret from a game's generation-2 build meta (zlib JSON).</summary>
    public async Task<(string ClientId, string ClientSecret)?> ResolveClientCredsAsync(long gogId, string platform = "windows", CancellationToken ct = default)
    {
        var (cid, sec) = await ResolveClientMetaAsync(gogId, platform, ct);
        return string.IsNullOrEmpty(cid) || string.IsNullOrEmpty(sec) ? null : (cid!, sec!);
    }

    /// <summary>THE build-meta read: builds?generation=2 -> items[0].link -> meta (zlib JSON, plain fallback) ->
    /// clientId + clientSecret, either null when absent. ResolveClientCredsAsync and ResolveClientIdAsync were two
    /// line-for-line copies of this walk (sweep 2 refactor); they differ only in what they need from the answer.</summary>
    private async Task<(string? ClientId, string? ClientSecret)> ResolveClientMetaAsync(long gogId, string platform, CancellationToken ct)
    {
        var session = await _auth.EnsureAuthenticatedAsync(ct);
        var buildsUrl = $"https://content-system.gog.com/products/{gogId}/os/{platform}/builds?generation=2";
        using var br = new HttpRequestMessage(HttpMethod.Get, buildsUrl);
        br.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        using var bres = await _http.SendAsync(br, ct);
        if (!bres.IsSuccessStatusCode) return (null, null);
        using var bdoc = JsonDocument.Parse(await bres.Content.ReadAsStringAsync(ct));
        if (!bdoc.RootElement.TryGetProperty("items", out var items) || items.GetArrayLength() == 0) return (null, null);
        var link = items[0].TryGetProperty("link", out var l) ? l.GetString() : null;
        if (string.IsNullOrEmpty(link)) return (null, null);
        using var mres = await _http.GetAsync(link, ct);
        if (!mres.IsSuccessStatusCode) return (null, null);
        var bytes = await mres.Content.ReadAsByteArrayAsync(ct);
        string metaJson;
        try
        {
            using var ms = new MemoryStream(bytes);
            using var z = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionMode.Decompress);
            using var sr = new StreamReader(z);
            metaJson = sr.ReadToEnd();
        }
        catch { metaJson = System.Text.Encoding.UTF8.GetString(bytes); }
        try
        {
            using var mdoc = JsonDocument.Parse(metaJson);
            var root = mdoc.RootElement;
            var cid = root.TryGetProperty("clientId", out var c) ? c.GetString() : null;
            var sec = root.TryGetProperty("clientSecret", out var s) ? s.GetString() : null;
            return (cid, sec);
        }
        catch { return (null, null); }
    }

    public async Task<IReadOnlyList<SaveProbe>> DiagnoseSavesAsync(string? clientId, string? spaceId, CancellationToken ct = default)
    {
        var session = await _auth.EnsureAuthenticatedAsync(ct);
        var probes = new List<SaveProbe>();
        async Task Probe(string key, string? id)
        {
            if (string.IsNullOrEmpty(id)) { probes.Add(new SaveProbe(key, "(none)", -1, 0)); return; }
            var url = $"https://cloudstorage.gog.com/v1/{session.UserId}/{id}";
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
                req.Headers.Accept.ParseAdd("application/json");
                using var res = await _http.SendAsync(req, ct);
                int count = 0;
                if (res.IsSuccessStatusCode)
                    try { count = ParseSaveList(await res.Content.ReadAsStringAsync(ct), url).Count; } catch { }
                probes.Add(new SaveProbe(key, url, (int)res.StatusCode, count));
            }
            catch { probes.Add(new SaveProbe(key, url, -1, 0)); }
        }
        await Probe("clientId (build meta)", clientId);
        await Probe("space_id (discovery)", spaceId);
        return probes;
    }

    /// <summary>Page size of the containers listing. One page was all that was ever asked for before sweep 2 #4,
    /// so an account with more than 50 games with saves silently lost the rest.</summary>
    internal const int ContainerPageSize = 50;
    /// <summary>A ceiling on the paging loop: a server that never returns a short page must not spin forever.</summary>
    internal const int ContainerMaxPages = 200;

    /// <summary>Every cloud-save container of the signed-in account, ALL pages. THROWS on a non-2xx answer
    /// (404 = the account has no containers, read as empty): an empty list is a fact ("no saves"), and the
    /// reconciler replaces the account's entries with it, so a 500 or a 403 read as empty wiped every game's
    /// cloud entry for that account (sweep 2 #3). Callers already treat a throw as "keep as last seen".</summary>
    public async Task<IReadOnlyList<CloudContainer>> ListContainersAsync(CancellationToken ct = default)
    {
        var session = await _auth.EnsureAuthenticatedAsync(ct);
        var all = new List<CloudContainer>();
        for (int page = 1; page <= ContainerMaxPages; page++)
        {
            var url = $"https://cloudstorage.gog.com/v2/users/{session.UserId}/containers?page={page}&limit={ContainerPageSize}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
            req.Headers.Accept.ParseAdd("application/json");
            using var res = await _http.SendAsync(req, ct);
            if (res.StatusCode == System.Net.HttpStatusCode.NotFound) break;
            res.EnsureSuccessStatusCode();

            var json = MaybeGunzip(await res.Content.ReadAsByteArrayAsync(ct));
            all.AddRange(ParseContainers(json));
            if (CountItems(json) < ContainerPageSize) break;   // a short page is the last one
        }
        return all;
    }

    /// <summary>Raw entries on a page (ParseContainers drops entries with no product, so its count cannot
    /// say whether the page was full).</summary>
    internal static int CountItems(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array
            ? items.GetArrayLength() : 0;
    }

    internal static IReadOnlyList<CloudContainer> ParseContainers(string json)
    {
        var list = new List<CloudContainer>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var it in items.EnumerateArray())
        {
            long pid = 0, quota = 0;
            if (it.TryGetProperty("games", out var games) && games.ValueKind == JsonValueKind.Array && games.GetArrayLength() > 0)
            {
                var g0 = games[0];
                if (g0.TryGetProperty("id", out var idEl)) pid = idEl.GetInt64();
                if (g0.TryGetProperty("quota", out var qEl)) quota = qEl.GetInt64();
            }
            long size = 0; int files = 0; string space = ""; DateTimeOffset? updated = null;
            if (it.TryGetProperty("container", out var c))
            {
                if (c.TryGetProperty("size", out var s)) size = s.GetInt64();
                if (c.TryGetProperty("files", out var f)) files = f.GetInt32();
                if (c.TryGetProperty("space_id", out var sp)) space = sp.GetString() ?? "";
                if (quota == 0 && c.TryGetProperty("quota", out var cq)) quota = cq.GetInt64();
                // Container change-time when GOG supplies it (field name varies). Primary change signal.
                foreach (var key in new[] { "date_updated", "last_modified", "modified", "updated", "updated_at" })
                    if (updated is null && c.TryGetProperty(key, out var dEl) &&
                        dEl.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(dEl.GetString(), out var dv))
                        updated = dv.ToUniversalTime();
            }
            if (pid > 0) list.Add(new CloudContainer(pid, size, files, space, quota, updated));
        }
        return list;
    }

    /// <summary>GOG may gzip cloud-storage responses and the shared HttpClient doesn't auto-decompress:
    /// detect the gzip magic and inflate when present, else read as UTF-8.</summary>
    private static string MaybeGunzip(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0x1f && bytes[1] == 0x8b)
        {
            using var ms = new System.IO.MemoryStream(bytes);
            using var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionMode.Decompress);
            using var sr = new System.IO.StreamReader(gz);
            return sr.ReadToEnd();
        }
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    /// <summary>Resolves a game's Galaxy clientId from build metadata: builds?generation=2 -> items[0].link ->
    /// meta (zlib JSON, plain fallback) -> clientId. Null if no builds / no clientId.</summary>
    public async Task<string?> ResolveClientIdAsync(long gogId, string platform = "windows", CancellationToken ct = default)
        => (await ResolveClientMetaAsync(gogId, platform, ct)).ClientId;

    /// <summary>Parses the save-list JSON into entries, tolerant of field-name variants: reads the common
    /// keys for name/path, size and timestamp.</summary>
    internal static IReadOnlyList<CloudSaveFile> ParseSaveList(string json, string baseUrl = "")
    {
        var list = new List<CloudSaveFile>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            JsonElement arr = root;
            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var key in new[] { "items", "files", "objects", "data" })
                    if (root.TryGetProperty(key, out var a) && a.ValueKind == JsonValueKind.Array) { arr = a; break; }
            }
            if (arr.ValueKind != JsonValueKind.Array) return list;

            foreach (var e in arr.EnumerateArray())
            {
                string name = FirstString(e, "name", "path", "key", "filename") ?? "";
                if (name.Length == 0) continue;
                long size = FirstLong(e, "size", "content_length", "bytes") ?? 0;
                DateTimeOffset? mod = null;
                var ts = FirstString(e, "last_modified", "modified", "date", "mtime");
                if (ts is not null && DateTimeOffset.TryParse(ts, out var parsed)) mod = parsed;
                // When no per-file URL is supplied, the download is base + /{name}.
                string dl = FirstString(e, "download_url", "url", "href")
                            ?? (baseUrl.Length > 0 ? $"{baseUrl}/{Uri.EscapeDataString(name).Replace("%2F", "/")}" : "");
                list.Add(new CloudSaveFile(name, size, mod, dl));
            }
        }
        catch { /* malformed body -> empty list; caller reports "no saves" cleanly */ }
        return list;
    }

    /// <summary>Download one game's saves into a fresh timestamped local save directory. Returns the
    /// local save path and the number of files written. Never overwrites prior localSaves.</summary>
    public async Task<(string LocalSaveDir, int FilesWritten, int FilesExpected)> DownloadLocalSaveAsync(
        CloudGame game, string gameDir, CancellationToken ct = default)
    {
        // Mint a game-scoped token; without it the per-game cloud endpoint 403s everything.
        var auth = await AuthorizeGameAsync(game.GogId, "windows", ct);
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var localSaveDir = Path.Combine(gameDir, "cloud-saves", stamp);
        Directory.CreateDirectory(localSaveDir);

        int written = 0; int expected = 0;
        try
        {
        var saves = auth is null ? Array.Empty<CloudSaveFile>()
                                 : await ListSavesAsync(auth.ClientId, auth.AccessToken, ct);
        expected = saves.Count(s => !string.IsNullOrEmpty(s.DownloadUrl));
        foreach (var save in saves)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(save.DownloadUrl)) continue;

            // Preserve the save's relative path (e.g. "saves/Orc 02 - 001.sav"), kept inside the dated folder.
            var dest = ResolveSaveTarget(localSaveDir, save);
            if (dest is null) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            if (await DownloadToAsync(save.DownloadUrl, auth!.AccessToken, dest, save.SizeBytes, ct)) written++;
        }
        }
        catch
        {
            // A capture that blew up is not a snapshot: between files it is a partial set, mid-file it holds a
            // truncated save. Left on disk it counted as the newest local save and retention pruned a
            // COMPLETE older one to make room for it (sweep 2 #6). Any failure, not only a cancel: the whole
            // dated folder goes.
            try { Directory.Delete(localSaveDir, recursive: true); } catch { /* best-effort cleanup */ }
            throw;
        }
        // Nothing came back (e.g. the endpoint 403'd): remove the empty timestamped folder -- a backup
        // tool must never make "nothing" look like "something".
        if (written == 0)
            try { Directory.Delete(localSaveDir, recursive: true); } catch { /* best-effort cleanup */ }
        return (localSaveDir, written, expected);
    }

    /// <summary>"Save As" / restore: download a game's current cloud saves FLAT into <paramref
    /// name="targetDir"/> (preserving each save's own relative path, e.g. "__default/data0.bin"), not a
    /// timestamped local save. For dropping saves straight into a game's live save folder. Returns files written.</summary>
    public async Task<int> ExportSavesAsync(long gogId, string targetDir, CancellationToken ct = default)
    {
        var auth = await AuthorizeGameAsync(gogId, "windows", ct);
        if (auth is null) return 0;
        var saves = await ListSavesAsync(auth.ClientId, auth.AccessToken, ct);
        Directory.CreateDirectory(targetDir);
        int written = 0;
        foreach (var save in saves)
        {
            if (ct.IsCancellationRequested) break;
            if (string.IsNullOrEmpty(save.DownloadUrl)) continue;
            var dest = ResolveSaveTarget(targetDir, save);
            if (dest is null) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            if (await DownloadToAsync(save.DownloadUrl, auth.AccessToken, dest, save.SizeBytes, ct)) written++;
        }
        return written;
    }

    private static string? FirstString(JsonElement e, params string[] keys)
    {
        foreach (var k in keys)
            if (e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String) return v.GetString();
        return null;
    }

    private static long? FirstLong(JsonElement e, params string[] keys)
    {
        foreach (var k in keys)
            if (e.TryGetProperty(k, out var v))
            {
                if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
                if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var s)) return s;
            }
        return null;
    }
}
