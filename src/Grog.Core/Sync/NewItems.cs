// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System.Collections.Generic;
using System.Linq;
using Grog.Core.Manifest;
using Grog.Core.Models;

namespace Grog.Core.Sync;

public static class NewItems
{
    public static IReadOnlyList<LibraryItem> Flagged(LibraryManifest manifest)
        => manifest.Items.Where(i => i.IsNew).OrderBy(i => i.Title, System.StringComparer.OrdinalIgnoreCase).ToList();

    public static int Count(LibraryManifest manifest) => manifest.Items.Count(i => i.IsNew);

    public static int Clear(LibraryManifest manifest, IEnumerable<long> gogIds)
    {
        var want = new HashSet<long>(gogIds);
        int n = 0;
        foreach (var item in manifest.Items)
            if (item.IsNew && want.Contains(item.GogId)) { item.IsNew = false; n++; }
        return n;
    }

    public static int ClearAll(LibraryManifest manifest)
    {
        int n = 0;
        foreach (var item in manifest.Items)
            if (item.IsNew) { item.IsNew = false; n++; }
        return n;
    }

    public static bool ClearIfComplete(LibraryManifest manifest, long gogId, Scope scope)
    {
        var item = manifest.ItemById(gogId);
        if (item is null || !item.IsNew) return false;
        if (!BackupScope.IsComplete(item, scope)) return false;
        item.IsNew = false;
        return true;
    }
}
