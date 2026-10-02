// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
//
// (New items 09-09) The "New" flag, in one place. An item is New when a scan ADDED it to this library
// (the very first scan of an empty library seeds instead from GOG's own isNew). It is ITEM level only:
// a superseded installer stays Outdated and a fresh extra on an owned game stays Not Backed Up -- there
// is no second badge concept here. New clears exactly two ways: the item becomes fully backed up in
// scope, or someone clears it explicitly. Every mutator here is PURE graph work: callers hold the
// manifest gate and save.
using System.Collections.Generic;
using System.Linq;
using Grog.Core.Manifest;
using Grog.Core.Models;

namespace Grog.Core.Sync;

public static class NewItems
{
    /// <summary>The items currently flagged New, in title order.</summary>
    public static IReadOnlyList<LibraryItem> Flagged(LibraryManifest manifest)
        => manifest.Items.Where(i => i.IsNew).OrderBy(i => i.Title, System.StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>How many items are flagged New. The prompt, the rail line and the header button read this.</summary>
    public static int Count(LibraryManifest manifest) => manifest.Items.Count(i => i.IsNew);

    /// <summary>Clear the flag on the named items. Returns how many actually changed.</summary>
    public static int Clear(LibraryManifest manifest, IEnumerable<long> gogIds)
    {
        var want = new HashSet<long>(gogIds);
        int n = 0;
        foreach (var item in manifest.Items)
            if (item.IsNew && want.Contains(item.GogId)) { item.IsNew = false; n++; }
        return n;
    }

    /// <summary>Clear every flag. Returns how many changed.</summary>
    public static int ClearAll(LibraryManifest manifest)
    {
        int n = 0;
        foreach (var item in manifest.Items)
            if (item.IsNew) { item.IsNew = false; n++; }
        return n;
    }

    /// <summary>The AUTO-clear: an item that is now complete against <paramref name="scope"/> is no longer
    /// new to you -- you have it. Completeness is never re-derived here; it is BackupScope's answer, the same
    /// one the grid and the queue use. Returns true when the flag changed (the caller saves).</summary>
    public static bool ClearIfComplete(LibraryManifest manifest, long gogId, Scope scope)
    {
        var item = manifest.ItemById(gogId);
        if (item is null || !item.IsNew) return false;
        if (!BackupScope.IsComplete(item, scope)) return false;
        item.IsNew = false;
        return true;
    }
}
