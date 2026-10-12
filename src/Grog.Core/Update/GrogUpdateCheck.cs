// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Grog.Core.Update;

public static class GrogUpdateCheck
{
    public const string LatestReleaseApi = "https://api.github.com/repos/KGScottCode/Grog/releases/latest";

    public readonly record struct Available(string Version, string Url);

    public static async Task<Available?> CheckAsync(HttpClient http, string currentVersion, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
            req.Headers.UserAgent.ParseAdd($"Grog/{currentVersion}");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return Evaluate(await resp.Content.ReadAsStringAsync(ct), currentVersion);
        }
        catch { return null; }
    }

    public static Available? Evaluate(string json, string currentVersion)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("draft", out var d) && d.GetBoolean()) return null;
            if (root.TryGetProperty("prerelease", out var p) && p.GetBoolean()) return null;

            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            var url = root.TryGetProperty("html_url", out var u) ? u.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag) || string.IsNullOrWhiteSpace(url)) return null;

            var latest = ParseVersion(tag);
            var current = ParseVersion(currentVersion);
            if (latest is null || current is null) return null;
            return latest > current ? new Available(latest.ToString(), url) : null;
        }
        catch { return null; }
    }

    public static Version? ParseVersion(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var bare = s.Trim().TrimStart('v', 'V');
        if (!bare.Contains('.')) bare += ".0";
        return Version.TryParse(bare, out var v) ? v : null;
    }
}
