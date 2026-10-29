// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using Grog.Core.Manifest;

namespace Grog.Core.Sync;

public sealed class ScopeSettings
{
    public bool IncludeGames { get; set; } = true;
    public bool IncludeExtras { get; set; } = true;

    public List<string> Languages { get; set; } = new();
    public bool LanguagesChosen { get; set; }

    public List<string>? ExtraLanguages { get; set; }
    public bool ExtraLanguagesChosen { get; set; }

    public List<string> Platforms { get; set; } = new();
    public bool PlatformsChosen { get; set; }

    public bool IncludePatches { get; set; } = true;

    public Scope ToScope() => new(IncludeGames, IncludeExtras,
        Languages.Count > 0 ? Languages : null,
        Platforms.Count > 0 ? Platforms : null,
        ExtraLanguagesChosen && ExtraLanguages is { } ex ? ex : null,
        IncludePatches);
}

public static class SavedScope
{
    public static ScopeSettings Set(IManifestStore store, bool? includeGames = null, bool? includeExtras = null,
        IReadOnlyCollection<string>? languages = null, IReadOnlyCollection<string>? platforms = null,
        IReadOnlyCollection<string>? extraLanguages = null, bool followGamesForExtras = false,
        bool? includePatches = null)
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
            if (includePatches is { } ip) s.IncludePatches = ip;
            result = s;
        });
        return result;
    }

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
