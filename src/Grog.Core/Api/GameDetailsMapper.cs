// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Globalization;
using System.Text;
using Grog.Core.Models;

namespace Grog.Core.Api;

public static class GameDetailsMapper
{
    public static LibraryItem ToLibraryItem(long gogId, GameDetails details, bool isDlc = false)
    {
        var item = new LibraryItem
        {
            GogId = gogId,
            Title = details.Title,
            Slug = Slugify(details.Title),
            SerialKey = details.CdKey,
            LastApiRefresh = DateTimeOffset.UtcNow,
        };

        foreach (var inst in details.Installers)
        {
            item.Files.Add(new GameFile
            {
                GameGogId = gogId,
                FileKey = inst.ManualUrl,
                Kind = IsPatch(inst.Name) ? FileKind.Patch : isDlc ? FileKind.DlcInstaller : FileKind.Installer,
                Name = inst.Name,
                Os = inst.Os,
                Language = inst.Language,
                Version = inst.Version,
                ExpectedSizeBytes = ParseSize(inst.SizeText),
                State = FileState.NotBackedUp,
            });
        }

        foreach (var extra in details.Extras)
        {
            item.Files.Add(new GameFile
            {
                GameGogId = gogId,
                FileKey = extra.ManualUrl,
                Kind = FileKind.Extra,
                Name = extra.Name,
                ExtraType = extra.Type,
                ExpectedSizeBytes = ParseSize(extra.SizeText),
                State = FileState.NotBackedUp,
            });
        }

        foreach (var dlc in details.Dlcs)
        {
            var child = ToLibraryItem(gogId, dlc, isDlc: true);
            item.Dlcs.Add(child);

            foreach (var f in child.Files)
            {
                f.GameGogId = gogId;
                if (f.Kind == FileKind.Installer) f.Kind = FileKind.DlcInstaller;
                if (string.IsNullOrEmpty(f.ExtraType) && f.Kind == FileKind.Extra)
                    f.ExtraType = "dlc";
                f.Name = string.IsNullOrEmpty(dlc.Title) ? f.Name : $"{f.Name} [{dlc.Title}]";
                item.Files.Add(f);
            }
        }

        return item;
    }

    public static long? ParseSize(string? sizeText)
    {
        if (string.IsNullOrWhiteSpace(sizeText)) return null;
        var s = sizeText.Trim();
        int spaceIdx = s.IndexOf(' ');
        if (spaceIdx <= 0) return null;

        var numberPart = s[..spaceIdx];
        var unitPart = s[(spaceIdx + 1)..].Trim().ToUpperInvariant();
        if (!double.TryParse(numberPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return null;

        double multiplier = unitPart switch
        {
            "B" => 1d,
            "KB" => 1024d,
            "MB" => 1024d * 1024,
            "GB" => 1024d * 1024 * 1024,
            "TB" => 1024d * 1024 * 1024 * 1024,
            _ => -1,
        };
        return multiplier < 0 ? null : (long)(value * multiplier);
    }

    public static string Slugify(string title) => Format.Naming.Slug(title);

    // GOG's details list patches among the installers, distinguished only by name: "Patch (1.0 to 1.1)",
    // "Patch 1.31", "Patch 1.31 (Part 1 of 2)". A version or bracket must follow, so "Patchwork" and a game
    // titled "Patch Quest" stay games.
    private static readonly System.Text.RegularExpressions.Regex PatchName =
        new(@"^\s*Patch\s*(\(|\d)", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    internal static bool IsPatch(string? name) => name is not null && PatchName.IsMatch(name);
}
