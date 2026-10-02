// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Grog.Core.Auth;

namespace Grog.Core.Api;

/// <summary>Research probe for GOG cloud-save endpoints. DIAGNOSTIC, not a feature. Resolves the per-game
/// clientId from the content-system build chain, then hits
/// https://cloudstorage-adapter.gog.com/v1/{clientId}/{userId}/... with Grog's Galaxy OAuth token.</summary>
public sealed class CloudSaveProbe
{
    private readonly HttpClient _http;
    private readonly IAuthService _auth;

    public CloudSaveProbe(HttpClient http, IAuthService auth)
    {
        _http = http;
        _auth = auth;
    }

    public sealed record ProbeStep(string Label, string Url, int Status, string ContentType, string BodySnippet);

    public async Task<List<ProbeStep>> ProbeAsync(long productId, string? clientIdOverride, CancellationToken ct = default)
    {
        var session = await _auth.EnsureAuthenticatedAsync(ct);
        var userId = string.IsNullOrEmpty(session.UserId) ? "USERID" : session.UserId;
        var steps = new List<ProbeStep>();

        async Task<ProbeStep> Try(string label, string url)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
                using var res = await _http.SendAsync(req, ct);
                var body = await res.Content.ReadAsStringAsync(ct);
                var snip = body.Length > 400 ? body[..400] + "…" : body;
                snip = snip.Replace("\n", " ").Replace("\r", " ");
                return new ProbeStep(label, url, (int)res.StatusCode,
                    res.Content.Headers.ContentType?.ToString() ?? "", snip);
            }
            catch (Exception ex) { return new ProbeStep(label, url, -1, "", $"EXCEPTION: {ex.Message}"); }
        }

        // Step 1: resolve the clientId. Prefer an override; otherwise walk the build chain.
        string? clientId = clientIdOverride;
        if (clientId is null)
        {
            var buildsUrl = $"https://content-system.gog.com/products/{productId}/os/windows/builds?generation=2";
            var buildsStep = await Try("DISCOVERY 1: builds list", buildsUrl);
            steps.Add(buildsStep);

            // Parse the first build's meta "link", fetch it, and look for clientId in the meta.
            try
            {
                using var doc = JsonDocument.Parse(await FetchRaw(buildsUrl, session.AccessToken, ct));
                if (doc.RootElement.TryGetProperty("items", out var items) && items.GetArrayLength() > 0)
                {
                    var first = items[0];
                    if (first.TryGetProperty("link", out var linkEl))
                    {
                        var metaUrl = linkEl.GetString()!;
                        var metaStep = await Try("DISCOVERY 2: build meta (zlib -- may look binary)", metaUrl);
                        steps.Add(metaStep);
                        // Build meta is often zlib-compressed; try to find clientId textually anyway.
                        var metaRaw = await FetchRaw(metaUrl, session.AccessToken, ct);
                        clientId = ExtractClientId(metaRaw);
                    }
                }
            }
            catch (Exception ex) { steps.Add(new ProbeStep("DISCOVERY parse", buildsUrl, -1, "", $"EXCEPTION: {ex.Message}")); }
        }

        steps.Add(new ProbeStep("RESOLVED clientId", "-", clientId is null ? -1 : 200, "",
            clientId ?? "(could not resolve from build meta -- may need Galaxy goggame-<id>.info file)"));

        // Step 2: hit the real endpoint shape with the resolved clientId (if we got one).
        if (clientId is not null)
        {
            await AddTry(steps, Try, "REAL: cloud_storage list",
                $"https://cloudstorage-adapter.gog.com/v1/{clientId}/{userId}/cloud_storage");
            await AddTry(steps, Try, "REAL: cloud_storage (no suffix)",
                $"https://cloudstorage-adapter.gog.com/v1/{clientId}/{userId}");
        }

        return steps;
    }

    private static async Task AddTry(List<ProbeStep> steps, Func<string,string,Task<ProbeStep>> t, string label, string url)
        => steps.Add(await t(label, url));

    private async Task<string> FetchRaw(string url, string token, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var res = await _http.SendAsync(req, ct);
        return await res.Content.ReadAsStringAsync(ct);
    }

    /// <summary>Best-effort: find a clientId in build-meta text. GOG's goggame info / meta contains
    /// a "clientId" (Galaxy client id, ~18-digit number distinct from the product id).</summary>
    private static string? ExtractClientId(string text)
    {
        var m = System.Text.RegularExpressions.Regex.Match(text, "\"clientId\"\\s*:\\s*\"?(\\d{10,})\"?");
        if (m.Success) return m.Groups[1].Value;
        m = System.Text.RegularExpressions.Regex.Match(text, "\"client_id\"\\s*:\\s*\"?(\\d{10,})\"?");
        return m.Success ? m.Groups[1].Value : null;
    }
}
