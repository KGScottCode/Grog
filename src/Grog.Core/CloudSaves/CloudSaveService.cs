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

public sealed class CloudSaveService
{
    private readonly HttpClient _http;
    private readonly IAuthService _auth;

    public CloudSaveService(HttpClient http, IAuthService auth)
    {
        _http = http;
        _auth = auth;
    }

    public Action<string>? Log { get; set; }

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

    internal static bool IsTrustedDownloadHost(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return false;
        var host = uri.Host;
        return host.Equals("gog.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".gog.com", StringComparison.OrdinalIgnoreCase);
    }

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

    public Task<IReadOnlyList<CloudGame>> ResolveGamesWithSavesAsync(
        IReadOnlyList<CloudGame> knownPairs, CancellationToken ct = default)
    {
        // TODO: replace with a GET to the master-list endpoint once captured; map results to CloudGame.
        return Task.FromResult(knownPairs);
    }

    public async Task<IReadOnlyList<CloudSaveFile>> ListSavesAsync(string clientId, string accessToken, CancellationToken ct = default)
    {
        var session = await _auth.EnsureAuthenticatedAsync(ct);
        var baseUrl = $"https://cloudstorage.gog.com/v1/{session.UserId}/{clientId}";
        using var req = new HttpRequestMessage(HttpMethod.Get, baseUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Headers.Accept.ParseAdd("application/json");
        using var res = await _http.SendAsync(req, ct);
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound) return Array.Empty<CloudSaveFile>();
        res.EnsureSuccessStatusCode();

        var json = await res.Content.ReadAsStringAsync(ct);
        return ParseSaveList(json, baseUrl);
    }

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

    public async Task<(string ClientId, string ClientSecret)?> ResolveClientCredsAsync(long gogId, string platform = "windows", CancellationToken ct = default)
    {
        var (cid, sec) = await ResolveClientMetaAsync(gogId, platform, ct);
        return string.IsNullOrEmpty(cid) || string.IsNullOrEmpty(sec) ? null : (cid!, sec!);
    }

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

    internal const int ContainerPageSize = 50;
    internal const int ContainerMaxPages = 200;

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
                foreach (var key in new[] { "date_updated", "last_modified", "modified", "updated", "updated_at" })
                    if (updated is null && c.TryGetProperty(key, out var dEl) &&
                        dEl.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(dEl.GetString(), out var dv))
                        updated = dv.ToUniversalTime();
            }
            if (pid > 0) list.Add(new CloudContainer(pid, size, files, space, quota, updated));
        }
        return list;
    }

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

    public async Task<string?> ResolveClientIdAsync(long gogId, string platform = "windows", CancellationToken ct = default)
        => (await ResolveClientMetaAsync(gogId, platform, ct)).ClientId;

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

    public async Task<(string LocalSaveDir, int FilesWritten, int FilesExpected)> DownloadLocalSaveAsync(
        CloudGame game, string gameDir, CancellationToken ct = default)
    {
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

            var dest = ResolveSaveTarget(localSaveDir, save);
            if (dest is null) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            if (await DownloadToAsync(save.DownloadUrl, auth!.AccessToken, dest, save.SizeBytes, ct)) written++;
        }
        }
        catch
        {
            try { Directory.Delete(localSaveDir, recursive: true); } catch { /* best-effort cleanup */ }
            throw;
        }
        if (written == 0)
            try { Directory.Delete(localSaveDir, recursive: true); } catch { /* best-effort cleanup */ }
        return (localSaveDir, written, expected);
    }

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
