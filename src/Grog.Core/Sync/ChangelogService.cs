// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Grog.Core.Auth;
using Grog.Core.Storage;

namespace Grog.Core.Sync;

/// <summary>
/// Fetches a game's GOG changelog (api.gog.com/products/{id}?expand=changelog), strips the HTML to
/// readable text, and stores it as .grog/changelogs/&lt;slug&gt;.json loaded only when viewed, so the
/// main manifest stays lean. No read/unread state -- this is reference text.
/// </summary>
public sealed class ChangelogService
{
    private readonly HttpClient _http;
    private readonly IAuthService _auth;
    private readonly string _changelogDir;

    public ChangelogService(HttpClient http, IAuthService auth, GrogPaths paths)
    {
        _http = http;
        _auth = auth;
        _changelogDir = Path.Combine(paths.ConfigDir, "changelogs");
    }

    public sealed record ChangelogEntry(long GogId, string Slug, string Title, string Text, DateTimeOffset FetchedAt);

    private string PathFor(string slug) => Path.Combine(_changelogDir, $"{Sanitize(slug)}.json");

    /// <summary>Fetch the changelog from GOG and store it. Returns the cleaned text (may be empty
    /// when the game has no changelog -- that's normal, not an error). Null on fetch failure.</summary>
    public async Task<string?> FetchAndStoreAsync(long gogId, string slug, string title, CancellationToken ct = default)
    {
        var session = await _auth.EnsureAuthenticatedAsync(ct);
        var url = $"https://api.gog.com/products/{gogId}?expand=changelog";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        using var res = await _http.SendAsync(req, ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return "";
        if (!res.IsSuccessStatusCode) return null;

        var json = await res.Content.ReadAsStringAsync(ct);
        string? raw = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("changelog", out var cl) && cl.ValueKind == JsonValueKind.String)
                raw = cl.GetString();
        }
        catch { return null; }

        var text = CleanHtml(raw);
        await StoreAsync(new ChangelogEntry(gogId, slug, title, text, DateTimeOffset.UtcNow), ct);
        return text;
    }

    /// <summary>Load a previously-stored changelog, or null if none is stored yet.</summary>
    public async Task<ChangelogEntry?> LoadAsync(string slug, CancellationToken ct = default)
    {
        var path = PathFor(slug);
        if (!File.Exists(path)) return null;
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<ChangelogEntry>(stream, cancellationToken: ct);
        }
        catch { return null; }
    }

    private async Task StoreAsync(ChangelogEntry entry, CancellationToken ct)
    {
        Directory.CreateDirectory(_changelogDir);
        var opts = new JsonSerializerOptions { WriteIndented = true };
        await using var stream = File.Create(PathFor(entry.Slug));
        await JsonSerializer.SerializeAsync(stream, entry, opts, ct);
    }

    /// <summary>Strip GOG's changelog HTML to readable plain text: turn block/break tags into
    /// newlines, drop the rest, decode entities, and collapse whitespace runs.</summary>
    internal static string CleanHtml(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var s = raw;
        // Block-ish tags -> newlines so structure survives.
        s = Regex.Replace(s, "<\\s*(br|/p|/div|/li|/h[1-6])\\s*/?\\s*>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, "<\\s*li[^>]*>", "\n• ", RegexOptions.IgnoreCase);
        // Remaining tags gone.
        s = Regex.Replace(s, "<.*?>", "");
        s = WebUtility.HtmlDecode(s);
        // Collapse >2 blank lines and trailing spaces.
        s = Regex.Replace(s, "[ \\t]+", " ");
        s = Regex.Replace(s, "\\n{3,}", "\n\n");
        return s.Trim();
    }

    private static string Sanitize(string slug)
    {
        var cleaned = string.IsNullOrWhiteSpace(slug) ? "unknown" : slug;
        foreach (var c in Path.GetInvalidFileNameChars()) cleaned = cleaned.Replace(c, '_');
        return cleaned;
    }
}
