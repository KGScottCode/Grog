// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Globalization;
using System.Text;
using Grog.Core.Models;

namespace Grog.Core.Api;

/// <summary>Turns a parsed <see cref="GameDetails"/> (API shape) into a <see cref="LibraryItem"/> (domain
/// shape). Pure and side-effect free: no HTTP, no disk.</summary>
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
                Kind = isDlc ? FileKind.DlcInstaller : FileKind.Installer,
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

        // GOG nests each DLC's own installers/extras inside dlcs[]; they are NOT in the base game's
        // lists. Pull them up onto this game so they're captured, tagged with their DLC title.
        foreach (var dlc in details.Dlcs)
        {
            var child = ToLibraryItem(gogId, dlc, isDlc: true);
            item.Dlcs.Add(child);

            foreach (var f in child.Files)
            {
                // Re-file the DLC's installers/extras onto the parent, noting their DLC origin.
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

    /// <summary>Parses GOG's human size strings ("1.5 GB") to approximate bytes for display/planning; exact
    /// bytes come from Content-Length at download time. Null when unparseable.</summary>
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

    /// <summary>Folder-safe name derived from the title; the one rule lives in <see cref="Format.Naming.Slug"/>.</summary>
    public static string Slugify(string title) => Format.Naming.Slug(title);
}
