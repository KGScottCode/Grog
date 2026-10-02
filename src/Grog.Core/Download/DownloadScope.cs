// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Grog.Core.Models;

namespace Grog.Core.Download;

/// <summary>
/// Declarative rules for which files a download run should include. Usable per-run (from CLI flags)
/// and persistable as saved defaults in the manifest. Kept as plain data so the GUI can bind to it
/// and the CLI can populate it from flags; <see cref="Selector"/> applies it.
/// </summary>
public sealed class DownloadScope
{
    // Content kind toggles.
    /// <summary>Also gates language packs: a language pack is an installer for its language, and no
    /// caller has ever wanted one without the other.</summary>
    public bool IncludeInstallers { get; set; } = true;
    public bool IncludePatches { get; set; } = true;
    public bool IncludeExtras { get; set; } = true;
    public bool IncludeDlcInstallers { get; set; } = true;

    /// <summary>Extra sub-types to include (soundtrack, artbook, manual, ...). Empty = all extra types.
    /// Matched case-insensitively against GameFile.ExtraType substrings.</summary>
    public HashSet<string> ExtraTypes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // OS / language.
    /// <summary>Preferred OSes in priority order, e.g. ["linux","windows"]. Empty = all.
    /// With <see cref="OsFallback"/>, only the highest-priority available OS per game is taken.</summary>
    public List<string> OsPriority { get; set; } = new();
    public bool OsFallback { get; set; }
    /// <summary>Language substrings to include (e.g. "english"). Empty = all.</summary>
    public List<string> Languages { get; set; } = new();

    // Game-level filters.
    /// <summary>Only these product types (game/mod/pack/dlc/movie). Empty = all.</summary>
    public HashSet<ProductType> Types { get; set; } = new();
    /// <summary>Regex applied to game title; null = no title filter.</summary>
    public string? TitleRegex { get; set; }

    // Blacklist: substrings; a file whose resolved/display name contains any is skipped.
    public List<string> Blacklist { get; set; } = new();

    public bool KindIncluded(FileKind k) => k switch
    {
        FileKind.Installer => IncludeInstallers,
        FileKind.Patch => IncludePatches,
        FileKind.LanguagePack => IncludeInstallers,
        FileKind.DlcInstaller => IncludeDlcInstallers,
        FileKind.Extra => IncludeExtras,
        _ => true,
    };
}

/// <summary>Applies a <see cref="DownloadScope"/> to the library, yielding the files to fetch.</summary>
public static class DownloadFileSelector
{
    public static IEnumerable<(LibraryItem game, GameFile file)> Select(
        IEnumerable<LibraryItem> items, DownloadScope scope)
    {
        Regex? titleRx = scope.TitleRegex is { Length: > 0 } tr
            ? new Regex(tr, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            : null;

        foreach (var g in items)
        {
            if (scope.Types.Count > 0 && !scope.Types.Contains(g.Type)) continue;
            if (titleRx is not null && !titleRx.IsMatch(g.Title)) continue;

            // Resolve the OS set for this game if OS priority + fallback is in play.
            HashSet<string>? allowedOs = null;
            if (scope.OsPriority.Count > 0)
            {
                var present = g.Files.Where(f => f.Kind is FileKind.Installer or FileKind.DlcInstaller)
                    .Select(f => f.Os).Where(s => !string.IsNullOrEmpty(s))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (scope.OsFallback)
                {
                    // Take only the first priority OS that this game actually has.
                    var chosen = scope.OsPriority.FirstOrDefault(p => present.Contains(p));
                    allowedOs = chosen is null ? new(StringComparer.OrdinalIgnoreCase)
                                               : new(new[] { chosen }, StringComparer.OrdinalIgnoreCase);
                }
                else
                {
                    // Take all listed OSes (no fallback).
                    allowedOs = new(scope.OsPriority, StringComparer.OrdinalIgnoreCase);
                }
            }

            foreach (var f in g.Files)
            {
                if (!scope.KindIncluded(f.Kind)) continue;

                if (f.Kind is FileKind.Installer or FileKind.DlcInstaller && allowedOs is not null
                    && !string.IsNullOrEmpty(f.Os) && !allowedOs.Contains(f.Os)) continue;

                if (scope.Languages.Count > 0 && !string.IsNullOrEmpty(f.Language)
                    && !scope.Languages.Any(l => f.Language.Contains(l, StringComparison.OrdinalIgnoreCase)))
                    continue;

                if (f.Kind == FileKind.Extra && scope.ExtraTypes.Count > 0)
                {
                    var et = f.ExtraType ?? "";
                    if (!scope.ExtraTypes.Any(t => et.Contains(t, StringComparison.OrdinalIgnoreCase))) continue;
                }

                if (scope.Blacklist.Count > 0)
                {
                    var hay = (f.Name ?? "") + " " + (f.ExtraType ?? "");
                    if (scope.Blacklist.Any(b => hay.Contains(b, StringComparison.OrdinalIgnoreCase))) continue;
                }

                yield return (g, f);
            }
        }
    }
}
