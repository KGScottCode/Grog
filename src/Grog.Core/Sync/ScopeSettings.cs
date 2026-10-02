// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using Grog.Core.Manifest;

namespace Grog.Core.Sync;

/// <summary>
/// The user's persisted backup scope (content, languages, platforms). Lives in the MANIFEST so it travels
/// with the backup; the GUI seeds it from legacy AppSettings on first load of a pre-scope manifest.
/// Contracts match <see cref="Scope"/>: EMPTY list = everything, <see cref="ExtraLanguages"/> null = "same
/// as Languages", and the *Chosen flags distinguish "wants everything" from "has not been asked".
/// </summary>
public sealed class ScopeSettings
{
    /// <summary>Back up games (installers/patches/DLC).</summary>
    public bool IncludeGames { get; set; } = true;
    /// <summary>Back up extras (soundtracks, manuals, wallpapers).</summary>
    public bool IncludeExtras { get; set; } = true;

    /// <summary>Languages to back up for GAMES. Empty = every language.</summary>
    public List<string> Languages { get; set; } = new();
    /// <summary>True once the user (or first-run seeding) has actually decided.</summary>
    public bool LanguagesChosen { get; set; }

    /// <summary>Languages to back up for EXTRAS. NULL means "same as Languages", NOT "all"; an EMPTY
    /// list still means every language. See <see cref="Scope"/> for why the split exists.</summary>
    public List<string>? ExtraLanguages { get; set; }
    /// <summary>True once extras have been given their OWN pick, as opposed to still following games.</summary>
    public bool ExtraLanguagesChosen { get; set; }

    /// <summary>Platforms to back up ("windows"/"mac"/"linux"). Empty = every platform.</summary>
    public List<string> Platforms { get; set; } = new();
    public bool PlatformsChosen { get; set; }

    /// <summary>The filter this scope imposes, in the ONE shared predicate vocabulary. Every consumer
    /// (GUI, CLI, stats) filters through the returned <see cref="Scope"/> - never re-derive the test.</summary>
    public Scope ToScope() => new(IncludeGames, IncludeExtras,
        Languages.Count > 0 ? Languages : null,
        Platforms.Count > 0 ? Platforms : null,
        ExtraLanguagesChosen && ExtraLanguages is { } ex ? ex : null);
}

/// <summary>The one writer of the manifest's saved scope for hosts that set it in a single step (the CLI's `scope set`).
/// Null arguments leave that part as it is; an empty list means "every language/platform".</summary>
public static class SavedScope
{
    public static ScopeSettings Set(IManifestStore store, bool? includeGames = null, bool? includeExtras = null,
        IReadOnlyCollection<string>? languages = null, IReadOnlyCollection<string>? platforms = null,
        IReadOnlyCollection<string>? extraLanguages = null, bool followGamesForExtras = false)
    {
        ScopeSettings result = null!;
        store.Mutate(m =>
        {
            var s = m.Scope ??= new ScopeSettings();
            if (includeGames is { } g) s.IncludeGames = g;
            if (includeExtras is { } e) s.IncludeExtras = e;
            if (languages is not null) { s.Languages = languages.ToList(); s.LanguagesChosen = true; }
            if (platforms is not null) { s.Platforms = platforms.ToList(); s.PlatformsChosen = true; }
            if (extraLanguages is not null) { s.ExtraLanguages = extraLanguages.ToList(); s.ExtraLanguagesChosen = true; }
            else if (followGamesForExtras) { s.ExtraLanguages = null; s.ExtraLanguagesChosen = false; }
            result = s;
        });
        return result;
    }

    /// <summary>Drop queued files the scope excludes: the queue, not the scope, decides future runs, so a resume
    /// would otherwise fetch a turned-off axis. <paramref name="scope"/> defaults to the saved scope (none saved:
    /// nothing is out of scope). Collected first, removed in one gated batch. Returns what left, for a host that
    /// must also cancel them in a live engine; its Count is the number pruned.</summary>
    public static IReadOnlyList<(long GogId, string FileKey)> PruneQueuedOutOfScope(IManifestStore store, Scope? scope = null)
    {
        var m = store.Current;
        if ((scope ?? m.Scope?.ToScope()) is not { } s || m.Downloads.IsEmpty) return Array.Empty<(long, string)>();
        var byId = new Dictionary<long, Grog.Core.Models.LibraryItem>();
        foreach (var i in m.Items) byId.TryAdd(i.GogId, i);
        var drop = new List<(long GogId, string FileKey)>();
        foreach (var q in m.Downloads.Snapshot())
        {
            if (!byId.TryGetValue(q.GogId, out var item)) continue;
            var f = item.Files.FirstOrDefault(x => x.FileKey == q.FileKey);
            if (f is not null && !s.Includes(f)) drop.Add((q.GogId, q.FileKey));
        }
        if (drop.Count > 0) store.Mutate(_ => { foreach (var (g, k) in drop) m.Downloads.Remove(g, k); });
        return drop;
    }
}
