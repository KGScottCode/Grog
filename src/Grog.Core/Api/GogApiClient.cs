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

/// <summary>Detail-pane art from a single v2/games call: the vertical `boxArtImage` (grid tile, cropped
/// square) and the galaxyBackgroundImage as GOG's 392-wide 16:9 subject crop (detail band). Either
/// may be null.</summary>
public sealed record GameArt(byte[]? BoxArt, byte[]? Galaxy)
{
    public static readonly GameArt Empty = new(null, null);
    /// <summary>Localization language names harvested from the same v2 response (e.g. "English").</summary>
    public IReadOnlyList<string> Languages { get; init; } = System.Array.Empty<string>();
    /// <summary>Age ratings by board ("pegi"/"esrb"/"usk"/"br" -> minimum age) from the same v2 response.</summary>
    public IReadOnlyDictionary<string, int> AgeRatings { get; init; } = new Dictionary<string, int>();
}

/// <summary>Grog's GOG HTTP client: account identity, the owned-product catalog, per-product detail, and the
/// art the grid and detail pane draw. Deliberately interface-free; shape any future test seam from this class.</summary>
public sealed class GogApiClient
{
    private readonly HttpClient _http;
    private readonly IAuthService _auth;

    /// <summary>The HttpClient the API calls should run on: gzip/brotli accepted and decompressed by the handler,
    /// which cuts the JSON listings to a fraction on the wire. For the metadata client only: the download client
    /// stays raw so a CDN body is the file's own bytes, hashed as they arrive.</summary>
    public static HttpClient CreateApiHttpClient(TimeSpan? timeout = null)
        => new(new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.All })
        { Timeout = timeout ?? TimeSpan.FromSeconds(60) };

    public GogApiClient(HttpClient http, IAuthService auth)
    {
        _http = http;
        _auth = auth;
    }

    // 429/5xx backoff: per-run in-memory, exponential 2s doubling to a 5-minute cap, Retry-After honored.
    // Transport failures never enter this schedule; see TransportTries below.
    // The resume gate is SHARED across concurrent requests so a parallel sweep backs off as one body.
    // 429 retries indefinitely; 5xx retries a few times then surfaces so a broken endpoint fails loudly.
    private static readonly TimeSpan ThrottleBase = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ThrottleMax = TimeSpan.FromMinutes(5);
    // 2, not 4 (owner call 09-01): a product with a BROKEN record 500s every time, and at 2s/4s/8s/16s
    // the old cap stalled every scan's tail ~30s for nothing. Two retries (~6s) still absorb a blip;
    // a persistent 500 is reported as unavailable and retried for free on the next scan anyway.
    private const int ServerErrorRetries = 2;
    // Transport failures (connection reset, client timeout) are LOCAL to the call: a fixed 1 s pause, three
    // tries, never the shared gate, so an offline machine fails each call in seconds instead of minutes.
    private const int TransportTries = 3;
    private static readonly TimeSpan TransportDelay = TimeSpan.FromSeconds(1);
    private int _throttleStrikes;                  // consecutive throttled responses; reset on any success
    private long _resumeAtTicks;                   // UTC ticks; the shared "nobody sends before" gate
    /// <summary>Wired by the host (GUI Activity log / CLI stderr): one line per wait, naming the delay.</summary>
    public Action<string>? OnThrottle { get; set; }
    /// <summary>Test seam: the suite swaps the real delay out so backoff tests run in milliseconds.</summary>
    internal Func<TimeSpan, CancellationToken, Task> DelayAsync = (d, c) => Task.Delay(d, c);
    /// <summary>Test seam: the current consecutive throttle strike count.</summary>
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
                // Local to this call: never a throttle strike, never the shared gate.
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
                Interlocked.Exchange(ref _resumeAtTicks, 0);   // the road is open again; drop the gate
                return response;
            }
            if (serverErr && ++serverErrors > ServerErrorRetries) return response;   // caller's error path

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
            // This request has SERVED its wait; drop the gate it raised (unless a newer throttle moved it)
            // so the loop does not queue behind its own gate for a second helping.
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

    /// <summary>Fetches /account/gameDetails/{id}.json into a typed GameDetails; null when GOG has none (some
    /// owned products 404 here). Parse failures throw GameDetailsParseException carrying the raw JSON.</summary>
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
            // GOG returns a bare "[]" for products with no account-level details (e.g. DLC, bonus items).
            return null;
        }
        catch (Exception ex)
        {
            throw new GameDetailsParseException(productId, json, ex);
        }
    }

    /// <summary>Public catalog lookup via api.gog.com/products/{id}; names the "no details" products (DLC,
    /// bonus items). No auth required. Returns null on 404.</summary>
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
        // Prefer GOG's product_card link; fall back to the slug. Never append the product id to
        // the slug -- that produces 404 URLs for two-ID products.
        string? storeUrl = null;
        if (root.TryGetProperty("links", out var links) && links.ValueKind == System.Text.Json.JsonValueKind.Object
            && links.TryGetProperty("product_card", out var pc))
            storeUrl = pc.GetString();
        if (string.IsNullOrEmpty(storeUrl) && root.TryGetProperty("slug", out var slugEl))
        {
            var slug = slugEl.GetString();
            if (!string.IsNullOrEmpty(slug)) storeUrl = "https://www.gog.com/game/" + slug;
        }

        // Capture the extra optional fields while we have the response.
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

    /// <summary>Account-scoped lookup for ids the public catalog 404s on (delisted or two-ID-split products
    /// still owned); getFilteredProducts titles them anyway. Pages through all results; null if not found.</summary>
    public async Task<GogProductInfo?> GetAccountProductInfoAsync(long productId, CancellationToken ct = default)
    {
        foreach (var p in await GetAllAccountProductsAsync(ct))
            if (p.Id == productId) return p;
        return null;
    }

    private IReadOnlyList<GogProductInfo>? _accountProductsCache;

    /// <summary>Fetches every owned product (title + url) across all getFilteredProducts pages, once, then
    /// caches for the process -- resolving many ids costs one paged fetch, not N.</summary>
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

    /// <summary>Fetches the full library via getFilteredProducts (all pages) as typed products. The
    /// authoritative listing: unlike /user/data/games it carries the pack/child relationships Grog needs.</summary>
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



    /// <summary>Acquisition dates from order history: product id -> EARLIEST order date it appears in.
    /// <paramref name="maxPages"/> caps the walk (1 = "only what is newest", the top-up; null = every page,
    /// the one-time backfill). Orders come newest first, so a capped walk is a walk of the most recent
    /// orders. A page that answers non-2xx ends the walk with what has been gathered, never throws: a
    /// cosmetic field must not fail a scan.</summary>
    public async Task<PurchaseDateSweep> GetPurchaseDatesAsync(int? maxPages = null,
                                                               CancellationToken ct = default)
    {
        var session = await _auth.EnsureAuthenticatedAsync(ct);
        var dates = new Dictionary<long, DateTimeOffset>();
        int page = 1, totalPages = 1, requests = 0;
        bool complete = false;   // set only by walking off the end of the last page
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
            if (!response.IsSuccessStatusCode) break;   // truncated: complete stays false
            var json = await response.Content.ReadAsStringAsync(ct);

            OrdersPage parsed;
            try { parsed = OrdersParser.Parse(json); }
            catch (JsonException) { break; }     // a shape change ends the walk; it never takes the scan with it
            if (parsed.TotalPages > 0) totalPages = parsed.TotalPages;
            OrdersParser.Fold(parsed.Orders, dates);

            page++;
            if (page > totalPages) { complete = true; break; }   // walked off the end: every page was read
            if (maxPages is { } cap && page > cap) break;        // deliberately capped: NOT complete
        } while (!ct.IsCancellationRequested);

        return new PurchaseDateSweep(dates, requests, complete && !ct.IsCancellationRequested);
    }

    /// <summary>Diagnostic: list every art link GOG publishes for a product, with the template resolved.
    /// The grid wants square-ish art and we only read two of these, so this shows what's really on offer.</summary>
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

        // _embedded also carries product images on some responses; include it so nothing is missed.
        if (doc.RootElement.TryGetProperty("_embedded", out var emb)
            && emb.TryGetProperty("product", out var prod))
            foreach (var p in prod.EnumerateObject())
                if (p.Name.Contains("image", StringComparison.OrdinalIgnoreCase)
                    || p.Name.Contains("icon", StringComparison.OrdinalIgnoreCase))
                    lines.Add($"product.{p.Name,-15} {p.Value}");

        return (lines, "");
    }

    /// <summary>Download just enough of an image to read its dimensions from the header. The grid wants
    /// square art and GOG publishes several shapes, so measure rather than assume which is which.</summary>
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

            // Total bytes: a range request reports the full length in Content-Range, so this costs 4 KB
            // rather than the whole image.
            long total = resp.Content.Headers.ContentRange?.Length ?? resp.Content.Headers.ContentLength ?? 0;
            var size = total > 0 ? Format.ByteFormat.Size(total) : "?";

            if (w == 0) return $"?          {size}";
            var shape = w == h ? "SQUARE" : w > h ? "wide" : "tall";
            return $"{w}x{h} {shape}".PadRight(18) + size;
        }
        catch { return "?"; }
    }

    /// <summary>Width/height straight from a PNG or JPEG header.</summary>
    private static (int W, int H) ReadSize(byte[] b)
    {
        // PNG: 8-byte signature, then IHDR with width/height as big-endian ints at 16..24.
        if (b.Length > 24 && b[0] == 0x89 && b[1] == 0x50)
            return (BE(b, 16), BE(b, 20));

        // JPEG: walk the markers to the start-of-frame, which carries the size.
        if (b.Length > 4 && b[0] == 0xFF && b[1] == 0xD8)
        {
            int i = 2;
            while (i + 9 < b.Length)
            {
                if (b[i] != 0xFF) { i++; continue; }
                byte marker = b[i + 1];
                int len = (b[i + 2] << 8) | b[i + 3];
                // SOF0..SOF15, excluding the non-frame markers in that range.
                if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
                    return ((b[i + 7] << 8) | b[i + 8], (b[i + 5] << 8) | b[i + 6]);
                i += 2 + len;
            }
        }
        return (0, 0);
    }

    private static int BE(byte[] b, int o) => (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];


    /// <summary>One v2/games call -> the two art assets the detail pane needs: the square `icon`
    /// (grid tile) and `galaxyBackgroundImage` as GOG's 392-wide 16:9 subject crop (detail band).
    /// Both come from the same response, so this costs one API call plus the two image GETs.</summary>
    public async Task<GameArt> FetchGameArtAsync(long productId, CancellationToken ct = default)
    {
        using var response = await SendWithBackoffAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"https://api.gog.com/v2/games/{productId}"), ct);
        if (!response.IsSuccessStatusCode) return GameArt.Empty;
        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("_links", out var links)) return GameArt.Empty;

        var boxArt = await FetchLinkImageAsync(links, "boxArtImage", null, ct);
        // Band art, best-first: galaxyBackgroundImage's 392-wide 16:9 subject crop, then the plain
        // backgroundImage hero, logo, box art -- the band is never blank if a title has any art at all.
        var galaxy = await FetchLinkImageAsync(links, "galaxyBackgroundImage", "_392.jpg", ct)
                  ?? await FetchLinkImageAsync(links, "backgroundImage", "_392.jpg", ct)
                  ?? await FetchLinkImageAsync(links, "logo", null, ct)
                  ?? boxArt;
        var (langs, ratings) = ParseV2Meta(doc.RootElement);
        return new GameArt(boxArt, galaxy) { Languages = langs, AgeRatings = ratings };
    }

    /// <summary>Harvest the non-art metadata Grog wants from the SAME v2/games response used for art:
    /// localization languages and per-board age ratings. Zero extra network cost.</summary>
    private static (List<string> Languages, Dictionary<string, int> AgeRatings) ParseV2Meta(JsonElement root)
    {
        var langs = new List<string>();
        var ratings = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty("_embedded", out var emb) || emb.ValueKind != JsonValueKind.Object)
            return (langs, ratings);

        // Languages: _embedded.localizations[]._embedded.language.name (distinct, order-preserving).
        if (emb.TryGetProperty("localizations", out var locs) && locs.ValueKind == JsonValueKind.Array)
            foreach (var loc in locs.EnumerateArray())
                if (loc.TryGetProperty("_embedded", out var le) && le.TryGetProperty("language", out var lang)
                    && lang.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String)
                {
                    var name = nm.GetString();
                    if (!string.IsNullOrEmpty(name) && !langs.Contains(name)) langs.Add(name);
                }

        // Ratings: any _embedded.<board>Rating.ageRating (pegiRating, esrbRating, uskRating, brRating, ...).
        foreach (var prop in emb.EnumerateObject())
            if (prop.Name.EndsWith("Rating", StringComparison.OrdinalIgnoreCase)
                && prop.Value.ValueKind == JsonValueKind.Object
                && prop.Value.TryGetProperty("ageRating", out var ageEl)
                && ageEl.TryGetInt32(out var age) && age > 0)
            {
                var board = prop.Name[..^"Rating".Length];   // "pegiRating" -> "pegi"
                ratings[board] = age;
            }

        return (langs, ratings);
    }

    /// <summary>Resolve a v2 `_links.{name}.href` to a concrete URL (stripping any {template} tokens),
    /// optionally swapping the bare-hash extension for a GOG size derivative (e.g. _392.jpg), and
    /// return the bytes. Null on any miss -- art is best-effort.</summary>
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

    /// <summary>Product ids GOG tags as "mod". GOG types these as ordinary games, so this tag list is the
    /// only authoritative way to spot a mod-as-game. Public catalog, no auth; empty set on error.</summary>
    public async Task<HashSet<long>> GetModTaggedProductIdsAsync(CancellationToken ct = default)
    {
        var ids = new HashSet<long>();
        int page = 1, totalPages = 1;
        do
        {
            var url = $"https://catalog.gog.com/v1/catalog?tags=mod&limit=100&page={page}";
            try
            {
                using var response = await SendWithBackoffAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct);
                if (!response.IsSuccessStatusCode) break;
                var json = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("pages", out var pagesEl) && pagesEl.TryGetInt32(out var tp))
                    totalPages = tp;

                if (root.TryGetProperty("products", out var products) && products.ValueKind == JsonValueKind.Array)
                {
                    foreach (var p in products.EnumerateArray())
                    {
                        // id may be a number or a numeric string depending on the endpoint version.
                        if (p.TryGetProperty("id", out var idEl))
                        {
                            if (idEl.ValueKind == JsonValueKind.Number && idEl.TryGetInt64(out var n)) ids.Add(n);
                            else if (idEl.ValueKind == JsonValueKind.String && long.TryParse(idEl.GetString(), out var s)) ids.Add(s);
                        }
                    }
                }
            }
            catch { break; }
            page++;
        } while (page <= totalPages && !ct.IsCancellationRequested);

        return ids;
    }

    /// <summary>Pulls GOG's human explanation out of a refused downlink's body (JSON envelope, bare text, or
    /// HTML): try the known JSON keys, fall back to raw text, empty for markup. Capped for UI display.</summary>
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
        // An HTML error page carries no useful sentence for the user; better to show our own fallback.
        if (body.StartsWith("<")) return "";
        return Cap(body);
    }

    private static string Cap(string s)
    {
        s = s.Replace("\r", " ").Replace("\n", " ").Trim();
        const int max = 300;
        return s.Length <= max ? s : s[..max].TrimEnd() + "...";
    }

    /// <summary>Resolves a manualUrl to the final CDN URL by following redirects without downloading the body;
    /// returns the resolved URL, server filename, content length, and checksum XML URL when GOG exposes one.</summary>
    /// <param name="probe">False skips the CDN HEAD/ranged-GET sizing probe (one round-trip per file);
    /// callers that already know the file's advertised size and name pass false, and take size + name
    /// from the manifest and the real GET's own headers instead.</param>
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

        // Entitlement refusal, not a transport error: 403 (or 404 for an empty catalog entry) with a human
        // explanation in the body. Must surface as its own type so the pipeline records it and stops instead
        // of retrying and condemning a never-fetched file as Corrupt; GOG's wording is carried for the UI.
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

        // The downlink body is either tiny JSON {"downlink","checksum"} or (after a followed 302) the CDN
        // file itself. Only buffer when the body is clearly the small JSON; buffering a multi-GB file blows
        // HttpClient's 2 GB limit. Otherwise the final redirected URI IS the CDN file url -- never buffer.
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

        // No explicit checksum URL in the JSON: derive it -- GOG's checksum XML is the CDN file URL with
        // ".xml" inserted before the query string, same token.
        checksumUrl ??= DeriveChecksumUrl(cdnUrl);

        DebugTrace?.Invoke($"resolve: cdn url = {Trunc(cdnUrl, 120)}");

        // Probe for filename + length. HEAD can hang on some CDNs, so bound it, fall back to a bounded
        // 1-byte ranged GET, and skip sizing if even that is slow.
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

    /// <summary>Optional diagnostic sink; when set, resolve/download steps report progress here.</summary>
    public Action<string>? DebugTrace { get; set; }

    /// <summary>The real filename from a CDN response: Content-Disposition, else the URL tail. Internal so the
    /// download engine can read it off the GET itself when the sizing probe was skipped.</summary>
    internal static string ExtractFileName(HttpResponseMessage resp, string cdnUrl)
    {
        var name = resp.Content.Headers.ContentDisposition?.FileNameStar
                   ?? resp.Content.Headers.ContentDisposition?.FileName
                   ?? DeriveFileName(resp.RequestMessage?.RequestUri ?? new Uri(cdnUrl));
        return name.Trim('"');
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    /// <summary>Fetches GOG's checksum XML (per-file MD5 + chunk data) and returns the file MD5, or null.</summary>
    public async Task<string?> FetchMd5Async(string checksumXmlUrl, CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync(checksumXmlUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) return null;
            var xml = await response.Content.ReadAsStringAsync(ct);
            // <file ... md5="..."> -- pull the file-level md5 attribute.
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

    /// <summary>GOG's checksum XML lives at the file URL with ".xml" inserted before the query string
    /// (same token): ".../setup_x.exe?&lt;token&gt;" -> ".../setup_x.exe.xml?&lt;token&gt;". No query -> append ".xml".</summary>
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
    // Extra fields from api.gog.com/products (all optional, safe defaults).
    public DateTimeOffset? ReleaseDate { get; init; }
    public bool InDevelopment { get; init; }
    public bool SupportsWindows { get; init; }
    public bool SupportsMac { get; init; }
    public bool SupportsLinux { get; init; }
    public List<string> Languages { get; init; } = new();
}

/// <summary>Raised when gameDetails JSON can't be parsed; carries the raw payload for diagnosis.</summary>
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
