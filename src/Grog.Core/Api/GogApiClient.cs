using System.Linq;
// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Grog.Core.Auth;
using Grog.Core.Format;

namespace Grog.Core.Api;

public sealed record GameArt(byte[]? BoxArt, byte[]? Galaxy)
{
    public static readonly GameArt Empty = new(null, null);
    public IReadOnlyList<string> Languages { get; init; } = System.Array.Empty<string>();
    public IReadOnlyDictionary<string, int> AgeRatings { get; init; } = new Dictionary<string, int>();
}

public sealed class GogApiClient
{
    private readonly HttpClient _http;
    private readonly IAuthService _auth;

    public static HttpClient CreateApiHttpClient(TimeSpan? timeout = null)
        => new(new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.All })
        { Timeout = timeout ?? TimeSpan.FromSeconds(60) };

    public GogApiClient(HttpClient http, IAuthService auth)
    {
        _http = http;
        _auth = auth;
    }

    private static readonly TimeSpan ThrottleBase = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ThrottleMax = TimeSpan.FromMinutes(5);
    private const int ServerErrorRetries = 2;
    private const int TransportTries = 3;
    private static readonly TimeSpan TransportDelay = TimeSpan.FromSeconds(1);
    private int _throttleStrikes;                  // consecutive throttled responses; reset on any success
    private long _resumeAtTicks;                   // UTC ticks; the shared "nobody sends before" gate
    public Action<string>? OnThrottle { get; set; }
    internal Func<TimeSpan, CancellationToken, Task> DelayAsync = (d, c) => Task.Delay(d, c);
    internal int ThrottleStrikes => _throttleStrikes;

    private async Task<HttpResponseMessage> SendWithBackoffAsync(
        Func<HttpRequestMessage> makeRequest, CancellationToken ct)
    {
        var serverErrors = 0;
        var transportFailures = 0;
        while (true)
        {
            var gate = new DateTimeOffset(Interlocked.Read(ref _resumeAtTicks), TimeSpan.Zero) - DateTimeOffset.UtcNow;
            if (gate > TimeSpan.Zero) await DelayAsync(gate, ct);

            using var request = makeRequest();
            HttpResponseMessage? response = null;
            string? transport = null;   // a connection-level failure, retried locally with a fixed pause
            HttpRequestException? transportEx = null;
            try
            {
                response = await _http.SendAsync(request, ct);
            }
            catch (HttpRequestException ex) { transport = ex.Message; transportEx = ex; }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                transport = "the request timed out" + (ex.InnerException is { } inner ? $" ({inner.Message})" : "");
            }
            if (response is null)
            {
                if (++transportFailures >= TransportTries)
                    throw transportEx ?? new HttpRequestException($"GOG could not be reached after {TransportTries} attempts: {transport}");
                OnThrottle?.Invoke($"GOG request failed ({transport}); retrying in {TransportDelay.TotalSeconds:F0}s");
                await DelayAsync(TransportDelay, ct);
                continue;
            }
            var status = (int)response.StatusCode;
            var throttled = status == 429;
            var serverErr = status >= 500;
            if (!throttled && !serverErr)
            {
                _throttleStrikes = 0;
                Interlocked.Exchange(ref _resumeAtTicks, 0);  
                return response;
            }
            if (serverErr && ++serverErrors > ServerErrorRetries) return response;  

            var strikes = Interlocked.Increment(ref _throttleStrikes);
            var hint = response.Headers.RetryAfter?.Delta
                    ?? (response.Headers.RetryAfter?.Date is { } at ? at - DateTimeOffset.UtcNow : (TimeSpan?)null);
            var delay = BackoffPolicy.Delay(strikes, ThrottleBase, ThrottleMax, hint);
            var gateTicks = (DateTimeOffset.UtcNow + delay).UtcTicks;
            Interlocked.Exchange(ref _resumeAtTicks, gateTicks);
            OnThrottle?.Invoke(throttled
                ? $"GOG is rate-limiting; waiting {delay.TotalSeconds:F0}s before retrying"
                : $"GOG returned {status}; retrying in {delay.TotalSeconds:F0}s");
            response.Dispose();
            await DelayAsync(delay, ct);
            Interlocked.CompareExchange(ref _resumeAtTicks, 0, gateTicks);
        }
    }

    public async Task<GogUserInfo> GetUserInfoAsync(CancellationToken ct = default)
    {
        var json = await GetAuthorizedAsync("https://embed.gog.com/userData.json", ct);
        var dto = JsonSerializer.Deserialize<UserDataDto>(json) ?? new UserDataDto();
        return new GogUserInfo(dto.UserId ?? "", dto.Username ?? "", dto.Email ?? "");
    }

    public async Task<IReadOnlyList<long>> GetOwnedProductIdsAsync(CancellationToken ct = default)
    {
        var json = await GetAuthorizedAsync("https://embed.gog.com/user/data/games", ct);
        var dto = JsonSerializer.Deserialize<OwnedDto>(json) ?? new OwnedDto();
        return dto.Owned;
    }

    public async Task<GameDetails?> GetGameDetailsAsync(long productId, CancellationToken ct = default)
    {
        var session = await _auth.EnsureAuthenticatedAsync(ct);
        var url = $"https://embed.gog.com/account/gameDetails/{productId}.json";
        using var response = await SendWithBackoffAsync(() =>
        {
            var r = new HttpRequestMessage(HttpMethod.Get, url);
            r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
            return r;
        }, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        try
        {
            return GameDetailsParser.Parse(json);
        }
        catch (NoGameDetailsException)
        {
            return null;
        }
        catch (Exception ex)
        {
            throw new GameDetailsParseException(productId, json, ex);
        }
    }

    public async Task<GogProductInfo?> GetProductInfoAsync(long productId, CancellationToken ct = default)
    {
        var url = $"https://api.gog.com/products/{productId}";
        using var response = await SendWithBackoffAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return null;

        string? title = root.TryGetProperty("title", out var t) ? t.GetString() : null;
        string? type = root.TryGetProperty("game_type", out var gt) ? gt.GetString() : null;
        string? storeUrl = null;
        if (root.TryGetProperty("links", out var links) && links.ValueKind == System.Text.Json.JsonValueKind.Object
            && links.TryGetProperty("product_card", out var pc))
            storeUrl = pc.GetString();
        if (string.IsNullOrEmpty(storeUrl) && root.TryGetProperty("slug", out var slugEl))
        {
            var slug = slugEl.GetString();
            if (!string.IsNullOrEmpty(slug)) storeUrl = "https://www.gog.com/game/" + slug;
        }

        DateTimeOffset? releaseDate = null;
        if (root.TryGetProperty("release_date", out var rd) && rd.ValueKind == System.Text.Json.JsonValueKind.String
            && DateTimeOffset.TryParse(rd.GetString(), out var parsedRd))
            releaseDate = parsedRd;

        bool inDev = false;
        if (root.TryGetProperty("in_development", out var idv) && idv.ValueKind == System.Text.Json.JsonValueKind.Object
            && idv.TryGetProperty("active", out var idAct) && idAct.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
            inDev = idAct.GetBoolean();

        bool win = false, mac = false, lin = false;
        if (root.TryGetProperty("content_system_compatibility", out var csc) && csc.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            if (csc.TryGetProperty("windows", out var w) && w.ValueKind is System.Text.Json.JsonValueKind.True) win = true;
            if (csc.TryGetProperty("osx", out var o) && o.ValueKind is System.Text.Json.JsonValueKind.True) mac = true;
            if (csc.TryGetProperty("linux", out var l) && l.ValueKind is System.Text.Json.JsonValueKind.True) lin = true;
        }

        var langs = new List<string>();
        if (root.TryGetProperty("languages", out var langEl) && langEl.ValueKind == System.Text.Json.JsonValueKind.Object)
            foreach (var lp in langEl.EnumerateObject())
                if (lp.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                    langs.Add(lp.Value.GetString() ?? lp.Name);

        return new GogProductInfo(productId, title ?? "", type ?? "", storeUrl ?? "")
        {
            ReleaseDate = releaseDate,
            InDevelopment = inDev,
            SupportsWindows = win,
            SupportsMac = mac,
            SupportsLinux = lin,
            Languages = langs,
        };
    }

    public async Task<GogProductInfo?> GetAccountProductInfoAsync(long productId, CancellationToken ct = default)
    {
        foreach (var p in await GetAllAccountProductsAsync(ct))
            if (p.Id == productId) return p;
        return null;
    }

    private IReadOnlyList<GogProductInfo>? _accountProductsCache;

    public async Task<IReadOnlyList<GogProductInfo>> GetAllAccountProductsAsync(CancellationToken ct = default)
    {
        if (_accountProductsCache is not null) return _accountProductsCache;

        var session = await _auth.EnsureAuthenticatedAsync(ct);
        var all = new List<GogProductInfo>();
        int page = 1, totalPages = 1;

        do
        {
            var url = $"https://embed.gog.com/account/getFilteredProducts?mediaType=1&page={page}";
            using var response = await SendWithBackoffAsync(() =>
            {
                var r = new HttpRequestMessage(HttpMethod.Get, url);
                r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
                return r;
            }, ct);
            if (!response.IsSuccessStatusCode) break;
            var json = await response.Content.ReadAsStringAsync(ct);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("totalPages", out var tp) && tp.TryGetInt32(out var t)) totalPages = t;

            if (root.TryGetProperty("products", out var products) && products.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in products.EnumerateArray())
                {
                    if (!p.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out var pid)) continue;
                    var title = p.TryGetProperty("title", out var ti) ? ti.GetString() ?? "" : "";
                    var purl = p.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                    var store = string.IsNullOrEmpty(purl) ? "" : "https://www.gog.com" + purl;
                    all.Add(new GogProductInfo(pid, title, "owned", store));
                }
            }
            page++;
        } while (page <= totalPages && !ct.IsCancellationRequested);

        _accountProductsCache = all;
        return all;
    }

    public async Task<IReadOnlyList<OwnedProduct>> GetLibraryProductsAsync(CancellationToken ct = default)
    {
        var session = await _auth.EnsureAuthenticatedAsync(ct);
        var all = new List<OwnedProduct>();
        int page = 1, totalPages = 1;
        do
        {
            var url = $"https://embed.gog.com/account/getFilteredProducts?mediaType=1&page={page}";
            using var response = await SendWithBackoffAsync(() =>
            {
                var r = new HttpRequestMessage(HttpMethod.Get, url);
                r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
                return r;
            }, ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);

            var parsed = FilteredProductsParser.Parse(json);
            totalPages = parsed.TotalPages > 0 ? parsed.TotalPages : 1;
            all.AddRange(parsed.Products);
            page++;
        } while (page <= totalPages && !ct.IsCancellationRequested);

        return all;
    }



    public async Task<PurchaseDateSweep> GetPurchaseDatesAsync(int? maxPages = null,
                                                               CancellationToken ct = default)
    {
        var session = await _auth.EnsureAuthenticatedAsync(ct);
        var dates = new Dictionary<long, DateTimeOffset>();
        int page = 1, totalPages = 1, requests = 0;
        bool complete = false;   
        do
        {
            var url = OrdersParser.PageUrl(page);
            using var response = await SendWithBackoffAsync(() =>
            {
                var r = new HttpRequestMessage(HttpMethod.Get, url);
                r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
                return r;
            }, ct);
            requests++;
            if (!response.IsSuccessStatusCode) break;   // truncated
            var json = await response.Content.ReadAsStringAsync(ct);

            OrdersPage parsed;
            try { parsed = OrdersParser.Parse(json); }
            catch (JsonException) { break; }    
            if (parsed.TotalPages > 0) totalPages = parsed.TotalPages;
            OrdersParser.Fold(parsed.Orders, dates);

            page++;
            if (page > totalPages) { complete = true; break; }   
            if (maxPages is { } cap && page > cap) break;        
        } while (!ct.IsCancellationRequested);

        return new PurchaseDateSweep(dates, requests, complete && !ct.IsCancellationRequested);
    }

    public async Task<(List<string>? Lines, string Error)> FetchGameLinksAsync(long productId, CancellationToken ct = default)
    {
        using var response = await SendWithBackoffAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"https://api.gog.com/v2/games/{productId}"), ct);
        if (!response.IsSuccessStatusCode) return (null, $"v2/games/{productId} -> HTTP {(int)response.StatusCode}");
        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("_links", out var links)) return (null, "no _links in v2 response");

        var lines = new List<string>();
        foreach (var link in links.EnumerateObject())
        {
            var href = link.Value.ValueKind == JsonValueKind.Object && link.Value.TryGetProperty("href", out var h)
                ? h.GetString() ?? "" : link.Value.ToString();
            var dims = await ProbeDimensionsAsync(href, ct);
            lines.Add($"{link.Name,-24} {dims,-30} {href}");
        }

        // _embedded also carries product images on some responses
        if (doc.RootElement.TryGetProperty("_embedded", out var emb)
            && emb.TryGetProperty("product", out var prod))
            foreach (var p in prod.EnumerateObject())
                if (p.Name.Contains("image", StringComparison.OrdinalIgnoreCase)
                    || p.Name.Contains("icon", StringComparison.OrdinalIgnoreCase))
                    lines.Add($"product.{p.Name,-15} {p.Value}");

        return (lines, "");
    }

    private async Task<string> ProbeDimensionsAsync(string url, CancellationToken ct)
    {
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return "";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 4096);
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return $"HTTP {(int)resp.StatusCode}";
            var b = await resp.Content.ReadAsByteArrayAsync(ct);
            var (w, h) = ReadSize(b);

            // Total bytes
            long total = resp.Content.Headers.ContentRange?.Length ?? resp.Content.Headers.ContentLength ?? 0;
            var size = total > 0 ? Format.ByteFormat.Size(total) : "?";

            if (w == 0) return $"?          {size}";
            var shape = w == h ? "SQUARE" : w > h ? "wide" : "tall";
            return $"{w}x{h} {shape}".PadRight(18) + size;
        }
        catch { return "?"; }
    }

    private static (int W, int H) ReadSize(byte[] b)
    {
        // PNG
        if (b.Length > 24 && b[0] == 0x89 && b[1] == 0x50)
            return (BE(b, 16), BE(b, 20));

        // JPEG
        if (b.Length > 4 && b[0] == 0xFF && b[1] == 0xD8)
        {
            int i = 2;
            while (i + 9 < b.Length)
            {
                if (b[i] != 0xFF) { i++; continue; }
                byte marker = b[i + 1];
                int len = (b[i + 2] << 8) | b[i + 3];

                if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
                    return ((b[i + 7] << 8) | b[i + 8], (b[i + 5] << 8) | b[i + 6]);
                i += 2 + len;
            }
        }
        return (0, 0);
    }

    private static int BE(byte[] b, int o) => (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];


    public async Task<GameArt> FetchGameArtAsync(long productId, CancellationToken ct = default)
    {
        using var response = await SendWithBackoffAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"https://api.gog.com/v2/games/{productId}"), ct);
        if (!response.IsSuccessStatusCode) return GameArt.Empty;
        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("_links", out var links)) return GameArt.Empty;

        var boxArt = await FetchLinkImageAsync(links, "boxArtImage", null, ct);
        var galaxy = await FetchLinkImageAsync(links, "galaxyBackgroundImage", "_392.jpg", ct)
                  ?? await FetchLinkImageAsync(links, "backgroundImage", "_392.jpg", ct)
                  ?? await FetchLinkImageAsync(links, "logo", null, ct)
                  ?? boxArt;
        var (langs, ratings) = ParseV2Meta(doc.RootElement);
        return new GameArt(boxArt, galaxy) { Languages = langs, AgeRatings = ratings };
    }

    private static (List<string> Languages, Dictionary<string, int> AgeRatings) ParseV2Meta(JsonElement root)
    {
        var langs = new List<string>();
        var ratings = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty("_embedded", out var emb) || emb.ValueKind != JsonValueKind.Object)
            return (langs, ratings);

        // Languages
        if (emb.TryGetProperty("localizations", out var locs) && locs.ValueKind == JsonValueKind.Array)
            foreach (var loc in locs.EnumerateArray())
                if (loc.TryGetProperty("_embedded", out var le) && le.TryGetProperty("language", out var lang)
                    && lang.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String)
                {
                    var name = nm.GetString();
                    if (!string.IsNullOrEmpty(name) && !langs.Contains(name)) langs.Add(name);
                }

        // Ratings
        foreach (var prop in emb.EnumerateObject())
            if (prop.Name.EndsWith("Rating", StringComparison.OrdinalIgnoreCase)
                && prop.Value.ValueKind == JsonValueKind.Object
                && prop.Value.TryGetProperty("ageRating", out var ageEl)
                && ageEl.TryGetInt32(out var age) && age > 0)
            {
                var board = prop.Name[..^"Rating".Length];   //"pegi"
                ratings[board] = age;
            }

        return (langs, ratings);
    }

    private async Task<byte[]?> FetchLinkImageAsync(JsonElement links, string name, string? sizeSuffix, CancellationToken ct)
    {
        if (!links.TryGetProperty(name, out var el) || !el.TryGetProperty("href", out var hrefEl)) return null;
        var href = hrefEl.GetString() ?? "";
        if (string.IsNullOrEmpty(href)) return null;
        var url = System.Text.RegularExpressions.Regex.Replace(href, @"\{[^}]*\}", "");
        if (sizeSuffix is not null)
        {
            var baseNoExt = System.Text.RegularExpressions.Regex.Replace(
                url, @"\.(jpg|png|webp)$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            url = baseNoExt + sizeSuffix;
        }
        try
        {
            using var img = await _http.GetAsync(url, ct);
            if (!img.IsSuccessStatusCode) return null;
            var data = await img.Content.ReadAsByteArrayAsync(ct);
            return data.Length > 0 ? data : null;
        }
        catch { return null; }
    }

    public async Task<HashSet<long>> GetModTaggedProductIdsAsync(CancellationToken ct = default)
    {
        var ids = new HashSet<long>();
        int page = 1, totalPages = 1;
        do
        {
            var url = $"https://catalog.gog.com/v1/catalog?tags=mod&limit=100&page={page}";
            using var response = await SendWithBackoffAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Mod catalog page {page} of {totalPages}: HTTP {(int)response.StatusCode}");
            {
                var json = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("pages", out var pagesEl) && pagesEl.TryGetInt32(out var tp))
                    totalPages = tp;

                if (root.TryGetProperty("products", out var products) && products.ValueKind == JsonValueKind.Array)
                {
                    foreach (var p in products.EnumerateArray())
                    {
                        if (p.TryGetProperty("id", out var idEl))
                        {
                            if (idEl.ValueKind == JsonValueKind.Number && idEl.TryGetInt64(out var n)) ids.Add(n);
                            else if (idEl.ValueKind == JsonValueKind.String && long.TryParse(idEl.GetString(), out var s)) ids.Add(s);
                        }
                    }
                }
            }
            page++;
        } while (page <= totalPages && !ct.IsCancellationRequested);
        ct.ThrowIfCancellationRequested();

        return ids;
    }

    internal static string ExtractRefusalReason(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        body = body.Trim();
        if (body.StartsWith("{") || body.StartsWith("["))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    foreach (var key in new[] { "message", "error_description", "reason", "detail", "error" })
                        if (doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                        {
                            var s = v.GetString();
                            if (!string.IsNullOrWhiteSpace(s)) return Cap(s!.Trim());
                        }
            }
            catch (JsonException) { /* not JSON after all -- fall through to the raw text */ }
            return "";
        }

        if (body.StartsWith("<")) return "";
        return Cap(body);
    }

    private static string Cap(string s)
    {
        s = s.Replace("\r", " ").Replace("\n", " ").Trim();
        const int max = 300;
        return s.Length <= max ? s : s[..max].TrimEnd() + "...";
    }

    public async Task<ResolvedDownload> ResolveDownloadAsync(string manualUrl, CancellationToken ct = default, bool probe = true)
    {
        var session = await _auth.EnsureAuthenticatedAsync(ct);
        var downlinkUrl = "https://embed.gog.com" + manualUrl;
        DebugTrace?.Invoke($"resolve: GET {downlinkUrl}");
        using var dlCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        dlCts.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Get, downlinkUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, dlCts.Token);
        DebugTrace?.Invoke($"resolve: downlink status={(int)response.StatusCode} final={response.RequestMessage?.RequestUri}");

        if (response.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.NotFound)
        {
            string reason;
            try { reason = ExtractRefusalReason(await response.Content.ReadAsStringAsync(dlCts.Token)); }
            catch { reason = ""; }
            if (string.IsNullOrWhiteSpace(reason))
                reason = $"GOG did not provide this file to your account ({(int)response.StatusCode}).";
            DebugTrace?.Invoke($"resolve: UNAVAILABLE ({(int)response.StatusCode}) {reason}");
            throw new GogUnavailableException(reason, (int)response.StatusCode);
        }
        response.EnsureSuccessStatusCode();

        string cdnUrl = response.RequestMessage?.RequestUri?.ToString() ?? downlinkUrl;
        string? checksumUrl = null;
        var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
        var bodyLen = response.Content.Headers.ContentLength;
        bool isMetadata = mediaType.Contains("json", StringComparison.OrdinalIgnoreCase)
                          || bodyLen is > 0 and < 65_536;
        if (isMetadata)
        {
            var body = await response.Content.ReadAsStringAsync(dlCts.Token);
            DebugTrace?.Invoke($"resolve: downlink body = {Trunc(body, 200)}");
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("downlink", out var dl)) cdnUrl = dl.GetString() ?? cdnUrl;
                if (doc.RootElement.TryGetProperty("checksum", out var cs)) checksumUrl = cs.GetString();
            }
            catch (JsonException) { DebugTrace?.Invoke("resolve: small body not JSON, using redirected url"); }
        }
        else DebugTrace?.Invoke($"resolve: body is the file (type={mediaType} len={bodyLen?.ToString() ?? "?"}); using redirected url, not buffering");

        checksumUrl ??= DeriveChecksumUrl(cdnUrl);

        DebugTrace?.Invoke($"resolve: cdn url = {Trunc(cdnUrl, 120)}");

        long? length = null;
        string fileName = "";
        if (!probe)
        {
            DebugTrace?.Invoke("resolve: probe skipped (caller supplies size + name)");
            return new ResolvedDownload(cdnUrl, fileName, length, checksumUrl);
        }
        try
        {
            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probeCts.CancelAfter(TimeSpan.FromSeconds(20));
            using var head = new HttpRequestMessage(HttpMethod.Head, cdnUrl);
            using var headResp = await _http.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, probeCts.Token);
            if (headResp.Content.Headers.ContentLength is { } cl) length = cl;
            fileName = ExtractFileName(headResp, cdnUrl);
            DebugTrace?.Invoke($"resolve: HEAD ok len={length?.ToString() ?? "?"} name={fileName}");
        }
        catch (Exception ex)
        {
            DebugTrace?.Invoke($"resolve: HEAD failed ({ex.GetType().Name}: {ex.Message}); trying ranged GET");
            try
            {
                using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                probeCts.CancelAfter(TimeSpan.FromSeconds(20));
                using var g = new HttpRequestMessage(HttpMethod.Get, cdnUrl);
                g.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
                using var gResp = await _http.SendAsync(g, HttpCompletionOption.ResponseHeadersRead, probeCts.Token);
                length = gResp.Content.Headers.ContentRange?.Length ?? gResp.Content.Headers.ContentLength;
                fileName = ExtractFileName(gResp, cdnUrl);
                DebugTrace?.Invoke($"resolve: ranged GET ok len={length?.ToString() ?? "?"} name={fileName}");
            }
            catch (Exception ex2)
            {
                DebugTrace?.Invoke($"resolve: ranged GET also failed ({ex2.Message}); proceeding without size");
                fileName = DeriveFileName(new Uri(cdnUrl));
            }
        }

        DebugTrace?.Invoke($"resolve: checksum url = {checksumUrl ?? "(none)"}");
        return new ResolvedDownload(cdnUrl, fileName, length, checksumUrl);
    }

    private Action<string>? _debugTrace;
    public Action<string>? DebugTrace { get => _debugTrace; set => _debugTrace = Grog.Core.Format.UrlRedaction.Wrap(value); }

    internal static string ExtractFileName(HttpResponseMessage resp, string cdnUrl)
    {
        var name = resp.Content.Headers.ContentDisposition?.FileNameStar
                   ?? resp.Content.Headers.ContentDisposition?.FileName
                   ?? DeriveFileName(resp.RequestMessage?.RequestUri ?? new Uri(cdnUrl));
        return name.Trim('"');
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    public async Task<string?> FetchMd5Async(string checksumXmlUrl, CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync(checksumXmlUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) return null;
            var xml = await response.Content.ReadAsStringAsync(ct);
            var m = System.Text.RegularExpressions.Regex.Match(xml, "md5=\"([0-9a-fA-F]{32})\"");
            return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
        }
        catch { return null; }
    }

    private static string DeriveFileName(Uri uri)
    {
        var last = uri.Segments.Length > 0 ? uri.Segments[^1] : "download";
        return Uri.UnescapeDataString(last);
    }

    private static string DeriveChecksumUrl(string fileUrl)
    {
        int q = fileUrl.IndexOf('?');
        return q >= 0 ? fileUrl[..q] + ".xml" + fileUrl[q..] : fileUrl + ".xml";
    }

    private async Task<string> GetAuthorizedAsync(string url, CancellationToken ct)
    {
        var session = await _auth.EnsureAuthenticatedAsync(ct);
        using var response = await SendWithBackoffAsync(() =>
        {
            var r = new HttpRequestMessage(HttpMethod.Get, url);
            r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
            return r;
        }, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    private sealed class UserDataDto
    {
        [JsonPropertyName("userId")] public string? UserId { get; set; }
        [JsonPropertyName("username")] public string? Username { get; set; }
        [JsonPropertyName("email")] public string? Email { get; set; }
    }

    private sealed class OwnedDto
    {
        [JsonPropertyName("owned")] public List<long> Owned { get; set; } = new();
    }
}

public sealed record GogUserInfo(string UserId, string Username, string Email);

public sealed record GogProductInfo(long Id, string Title, string GameType, string StoreUrl)
{
    public DateTimeOffset? ReleaseDate { get; init; }
    public bool InDevelopment { get; init; }
    public bool SupportsWindows { get; init; }
    public bool SupportsMac { get; init; }
    public bool SupportsLinux { get; init; }
    public List<string> Languages { get; init; } = new();
}

public sealed class GameDetailsParseException : Exception
{
    public long ProductId { get; }
    public string RawJson { get; }
    public GameDetailsParseException(long productId, string rawJson, Exception inner)
        : base($"Failed to parse gameDetails for product {productId}: {inner.Message}", inner)
    {
        ProductId = productId;
        RawJson = rawJson;
    }
}
