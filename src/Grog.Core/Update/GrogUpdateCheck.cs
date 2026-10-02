// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Grog.Core.Update;

/// <summary>
/// Tier-1 update check: one anonymous GET to GitHub's latest-release endpoint, a version compare, and
/// nothing else. Finds, never downloads -- same contract as the GOG version check, and the same silence
/// rules: a backup tool is often offline, so every failure is swallowed and the answer is simply null.
/// Full auto-update is deliberately out of scope until real users exist (owner decision, handoff 08-24).
/// </summary>
public static class GrogUpdateCheck
{
    public const string LatestReleaseApi = "https://api.github.com/repos/KGScottCode/Grog/releases/latest";

    /// <summary>A newer release: its bare version ("0.2.0") and the page a human reads about it on.</summary>
    public readonly record struct Available(string Version, string Url);

    /// <summary>Asks GitHub once. Null on ANY failure -- offline, rate-limited, malformed, not newer --
    /// because the only actionable answer is "there is a newer release at this URL".</summary>
    public static async Task<Available?> CheckAsync(HttpClient http, string currentVersion, CancellationToken ct = default)
    {
        try
        {
            // (09-19) GROG_UPDATE_API: a REHEARSAL seam. While the repo is private the real endpoint answers 404 to
            // an anonymous caller, so the whole path (request, headers, parse, the Settings row, the log line) could
            // not be exercised before going public. Point it at any public repo's releases/latest to walk it.
            var api = Environment.GetEnvironmentVariable("GROG_UPDATE_API") is { Length: > 0 } o ? o : LatestReleaseApi;
            using var req = new HttpRequestMessage(HttpMethod.Get, api);
            // GitHub's API refuses requests without a User-Agent; the version doubles as light telemetry-free
            // context for their logs.
            req.Headers.UserAgent.ParseAdd($"Grog/{currentVersion}");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return Evaluate(await resp.Content.ReadAsStringAsync(ct), currentVersion);
        }
        catch { return null; }
    }

    /// <summary>The testable core: latest-release JSON in, "newer than current" out. Split from the HTTP so
    /// the suite exercises tags, malformed payloads and downgrade cases without a network.</summary>
    public static Available? Evaluate(string json, string currentVersion)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            // Drafts and prereleases are not "the latest release" for update purposes; the endpoint already
            // excludes them, but a defensive check costs nothing and the fields are documented.
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

    /// <summary>"v0.1.0", "V0.1", "0.1.0" all parse; anything else is null. Mirrors AppVersionText's
    /// TrimStart('v','V') so the tag format the release workflow produces is exactly what parses.</summary>
    public static Version? ParseVersion(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var bare = s.Trim().TrimStart('v', 'V');
        // A bare major ("1") is not a Version; normalize to two parts so a lazy tag still compares.
        if (!bare.Contains('.')) bare += ".0";
        return Version.TryParse(bare, out var v) ? v : null;
    }
}
