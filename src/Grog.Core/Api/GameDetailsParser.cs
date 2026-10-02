// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
//
// Parses /account/gameDetails/{id}.json into flat typed records so sync code never touches raw JSON.
// GOG's "downloads" is an array of 2-tuples [language-string, os-map-object] that System.Text.Json
// cannot map automatically. Separate from the HTTP client so it unit-tests against fixtures offline.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Grog.Core.Api;

/// <summary>Flattened, typed view of a gameDetails response.</summary>
public sealed class GameDetails
{
    public string Title { get; init; } = "";
    public string? CdKey { get; init; }
    public List<InstallerFile> Installers { get; init; } = new();
    public List<ExtraFile> Extras { get; init; } = new();
    public List<GameDetails> Dlcs { get; init; } = new();
}

public sealed record InstallerFile(
    string ManualUrl, string Name, string Os, string Language, string? Version, string SizeText);

public sealed record ExtraFile(
    string ManualUrl, string Name, string Type, string SizeText);

public static class GameDetailsParser
{
    public static GameDetails Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        // A bare "[]" means an owned product with no account-level details (DLC, bonus items) --
        // "no details", not a parse failure; signal it distinctly so callers can skip quietly.
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            throw new NoGameDetailsException();
        return ParseElement(doc.RootElement);
    }

    private static GameDetails ParseElement(JsonElement root)
    {
        var installers = new List<InstallerFile>();
        var extras = new List<ExtraFile>();
        var dlcs = new List<GameDetails>();

        var title = GetString(root, "title") ?? "";
        var cdKey = CleanSerial(GetString(root, "cdKey"));
        if (string.IsNullOrWhiteSpace(cdKey)) cdKey = null;

        // downloads: array of [ "Language", { os: [files] } ]. Movies (and some non-game products)
        // instead use a FLAT array of file objects with no language/os nesting; handle both.
        if (root.TryGetProperty("downloads", out var downloads) && downloads.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in downloads.EnumerateArray())
            {
                // Nested shape: [ "English", { "windows": [...] } ]
                if (entry.ValueKind == JsonValueKind.Array && entry.GetArrayLength() >= 2
                    && entry[1].ValueKind == JsonValueKind.Object)
                {
                    var language = entry[0].ValueKind == JsonValueKind.String ? entry[0].GetString() ?? "" : "";
                    foreach (var osProp in entry[1].EnumerateObject())     // "windows" | "mac" | "linux"
                    {
                        if (osProp.Value.ValueKind != JsonValueKind.Array) continue;
                        foreach (var file in osProp.Value.EnumerateArray())
                        {
                            if (file.ValueKind != JsonValueKind.Object) continue;
                            installers.Add(new InstallerFile(
                                ManualUrl: GetString(file, "manualUrl") ?? "",
                                Name: GetString(file, "name") ?? "",
                                Os: osProp.Name,
                                Language: language,
                                Version: NullIfEmpty(GetString(file, "version")),
                                SizeText: GetSizeText(file)));
                        }
                    }
                }
                // Flat shape: a bare file object directly in the downloads array (movies).
                else if (entry.ValueKind == JsonValueKind.Object)
                {
                    installers.Add(new InstallerFile(
                        ManualUrl: GetString(entry, "manualUrl") ?? "",
                        Name: GetString(entry, "name") ?? "",
                        Os: "",
                        Language: "",
                        Version: NullIfEmpty(GetString(entry, "version")),
                        SizeText: GetSizeText(entry)));
                }
                // Anything else (unexpected shape) is skipped rather than crashing the sync.
            }
        }

        // extras: flat array of files with a "type"
        if (root.TryGetProperty("extras", out var extrasEl) && extrasEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var file in extrasEl.EnumerateArray())
            {
                extras.Add(new ExtraFile(
                    ManualUrl: GetString(file, "manualUrl") ?? "",
                    Name: GetString(file, "name") ?? "",
                    Type: GetString(file, "type") ?? "",
                    SizeText: GetSizeText(file)));
            }
        }

        // dlcs: array of nested gameDetails-shaped objects (recurse)
        if (root.TryGetProperty("dlcs", out var dlcsEl) && dlcsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var dlc in dlcsEl.EnumerateArray())
            {
                if (dlc.ValueKind == JsonValueKind.Object)
                    dlcs.Add(ParseElement(dlc));
            }
        }

        return new GameDetails { Title = title, CdKey = cdKey, Installers = installers, Extras = extras, Dlcs = dlcs };
    }

    private static string? GetString(JsonElement el, string prop)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    /// <summary>Reads "size" tolerantly: usually a human string ("1.5 GB"), sometimes a raw byte NUMBER.
    /// Numbers are normalized to an "N B" string ParseSize understands; strings pass through unchanged.</summary>
    private static string GetSizeText(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty("size", out var v))
            return "";
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number when v.TryGetInt64(out var n) && n > 0 => $"{n} B",
            _ => "",
        };
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary>Normalizes cdKey (plain text for single-key games, HTML spans for multi-key) to clean text:
    /// one line per &lt;span&gt;, tags stripped, entities decoded; a label span (ends ':') pairs with its value.</summary>
    internal static string? CleanSerial(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (!raw.Contains('<')) return raw.Trim();   // plain single key -- leave as-is

        // Pull the text out of each <span>...</span> in order.
        var spans = System.Text.RegularExpressions.Regex
            .Matches(raw, "<span[^>]*>(.*?)</span>", System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .Select(m => System.Net.WebUtility.HtmlDecode(
                System.Text.RegularExpressions.Regex.Replace(m.Groups[1].Value, "<.*?>", "")).Trim())
            .Where(s => s.Length > 0)
            .ToList();

        if (spans.Count == 0)
        {
            // No spans but had tags -- strip everything and return the decoded remainder.
            var stripped = System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(raw, "<.*?>", " "));
            return NullIfEmpty(System.Text.RegularExpressions.Regex.Replace(stripped, "\\s+", " ").Trim());
        }

        // Two common GOG layouts: (a) label ends with ':' before each key; (b) alternating name/key
        // spans with no colon. Prefer (b) when spans pair up evenly with serial-looking keys, else (a);
        // fall back to one span per line.
        var lines = new List<string>();

        bool looksLikeKey(string s) =>
            s.Length >= 8 && s.All(c => char.IsLetterOrDigit(c) || c is '-' or ' ');

        // (b) alternating name/key: even count, and the even-indexed (0-based) entries are names
        // while odd-indexed look like keys.
        bool alternating = spans.Count >= 2 && spans.Count % 2 == 0
            && Enumerable.Range(0, spans.Count).All(i => i % 2 == 1 ? looksLikeKey(spans[i]) : true)
            && Enumerable.Range(0, spans.Count).Where(i => i % 2 == 1).Any(i => looksLikeKey(spans[i]));

        if (alternating)
        {
            for (int i = 0; i + 1 < spans.Count; i += 2)
            {
                var name = spans[i].TrimEnd(':', ' ');
                lines.Add($"{name}: {spans[i + 1]}");
            }
        }
        else
        {
            for (int i = 0; i < spans.Count; i++)
            {
                var cur = spans[i];
                if (cur.EndsWith(":") && i + 1 < spans.Count)
                    lines.Add($"{cur} {spans[++i]}");
                else
                    lines.Add(cur);
            }
        }
        return string.Join("\n", lines);
    }
}

/// <summary>Signals that gameDetails returned no object (e.g. a bare "[]") -- no details exist.</summary>
public sealed class NoGameDetailsException : Exception
{
    public NoGameDetailsException() : base("gameDetails response contained no details (empty array).") { }
}
