// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Grog.Core.Api;

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

        if (root.TryGetProperty("downloads", out var downloads) && downloads.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in downloads.EnumerateArray())
            {
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
            }
        }

        // extras: flat with a "type"
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

    internal static string? CleanSerial(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (!raw.Contains('<')) return raw.Trim();  

        var spans = System.Text.RegularExpressions.Regex
            .Matches(raw, "<span[^>]*>(.*?)</span>", System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .Select(m => System.Net.WebUtility.HtmlDecode(
                System.Text.RegularExpressions.Regex.Replace(m.Groups[1].Value, "<.*?>", "")).Trim())
            .Where(s => s.Length > 0)
            .ToList();

        if (spans.Count == 0)
        {
            var stripped = System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(raw, "<.*?>", " "));
            return NullIfEmpty(System.Text.RegularExpressions.Regex.Replace(stripped, "\\s+", " ").Trim());
        }

        var lines = new List<string>();

        bool looksLikeKey(string s) =>
            s.Length >= 8 && s.All(c => char.IsLetterOrDigit(c) || c is '-' or ' ');

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

public sealed class NoGameDetailsException : Exception
{
    public NoGameDetailsException() : base("gameDetails response contained no details (empty array).") { }
}
