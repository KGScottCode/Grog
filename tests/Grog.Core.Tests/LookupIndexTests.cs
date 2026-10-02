// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Grog.Core.Download;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Tests.Framework;

/// <summary>The keyed lookups behind the per-entry hot paths (DownloadQueue by (GogId, FileKey), LibraryManifest
/// by GogId) must agree with a linear scan of the list they index, through every mutator.</summary>
[NewBatch]
[Trait("queue")]
public class LookupIndexTests
{
    static void AssertMatchesScan(DownloadQueue q, IEnumerable<(long, string)> probes, string when)
    {
        var list = q.Snapshot();
        foreach (var (id, key) in probes)
        {
            var scan = list.FirstOrDefault(x => x.GogId == id && x.FileKey == key);
            Assert.Equal(scan is not null, q.Contains(id, key), $"{when}: Contains({id},{key}) agrees with the scan");
            bool set = q.SetPlacement(id, key, null, scan?.Fits ?? true);
            Assert.Equal(scan is not null, set, $"{when}: SetPlacement({id},{key}) finds what the scan finds");
        }
    }

    static IEnumerable<(long, string)> Probes() => Enumerable.Range(1, 8).Select(i => ((long)i, "f" + i)).Append((99L, "zz"));

    [Test] void Queue_index_agrees_with_a_scan_through_every_mutator()
    {
        var q = new DownloadQueue();
        for (int i = 1; i <= 6; i++) q.Enqueue(i, "f" + i, "r");
        AssertMatchesScan(q, Probes(), "after Enqueue");

        Assert.False(q.Enqueue(3, "f3", "other"), "re-enqueue updates, never duplicates");
        Assert.Equal("other", q.Snapshot().Single(x => x.GogId == 3).TargetRootId, "the update landed on the indexed entry");

        Assert.True(q.Remove(2, "f2"), "remove a middle entry");
        Assert.False(q.Remove(2, "f2"), "gone from the index too");
        AssertMatchesScan(q, Probes(), "after Remove");

        q.MoveTo(6, "f6", 0);
        q.SetOrder(new List<(long, string)> { (5, "f5"), (1, "f1") });
        AssertMatchesScan(q, Probes(), "after reorder");
        Assert.Equal("5,1,6,3,4", string.Join(",", q.Snapshot().Select(x => x.GogId)), "SetOrder: named first, rest keep order");

        Assert.True(q.SetPlacement(4, "f4", "s", fits: false), "flag one won't-fit");
        Assert.True(q.SetPlacement(6, "f6", "s", fits: false), "flag another");
        var gone = q.RemoveWontFit();
        Assert.Equal("4,6", string.Join(",", gone.Select(x => x.GogId).OrderBy(x => x)), "both removed");
        AssertMatchesScan(q, Probes(), "after RemoveWontFit");

        Assert.True(q.Enqueue(2, "f2"), "a removed key can come back");
        Assert.True(q.Enqueue(4, "f4"), "so can a won't-fit one");
        AssertMatchesScan(q, Probes(), "after re-add");

        q.Clear();
        Assert.True(q.IsEmpty, "cleared");
        AssertMatchesScan(q, Probes(), "after Clear");
        Assert.True(q.Enqueue(1, "f1"), "enqueue after Clear starts a fresh index");
        AssertMatchesScan(q, Probes(), "after Clear + Enqueue");
    }

    [Test] void Queue_index_is_rebuilt_from_a_loaded_list()
    {
        var src = new DownloadQueue();
        src.Enqueue(1, "a", "r"); src.Enqueue(2, "b", "r", fits: false);
        var json = JsonSerializer.Serialize(src);
        var q = JsonSerializer.Deserialize<DownloadQueue>(json)!;
        Assert.True(q.Contains(1, "a") && q.Contains(2, "b"), "loaded entries are found");
        Assert.False(q.Contains(3, "c"), "absent stays absent");
        Assert.True(q.SetPlacement(2, "b", "s", fits: true), "loaded entry is updatable by key");
        Assert.True(q.Remove(1, "a"), "and removable");
        AssertMatchesScan(q, new[] { (1L, "a"), (2L, "b"), (3L, "c") }, "after load + mutate");

        q.Items = new List<QueuedDownload> { new() { GogId = 7, FileKey = "x" } };
        Assert.True(q.Contains(7, "x") && !q.Contains(2, "b"), "the loader's setter resets the index");
    }

    // A persisted list with one key twice: the first entry is the queue's, and SetOrder, Remove and the index agree.
    [Test] void Queue_loaded_with_duplicate_keys_keeps_the_first_entry_everywhere()
    {
        var q = new DownloadQueue
        {
            Items = new List<QueuedDownload>
            {
                new() { GogId = 1, FileKey = "a", TargetRootId = "first" },
                new() { GogId = 2, FileKey = "b" },
                new() { GogId = 1, FileKey = "a", TargetRootId = "second" },
            },
        };
        Assert.Equal(2, q.Snapshot().Count, "the later duplicate is dropped on load");
        Assert.Equal("first", q.Snapshot().Single(x => x.GogId == 1).TargetRootId, "the first entry survives");

        q.SetOrder(new List<(long, string)> { (1, "a") });
        Assert.Equal("first", q.Snapshot()[0].TargetRootId, "SetOrder names the same entry the index does");
        Assert.True(q.Remove(1, "a"), "removed by key");
        Assert.False(q.Contains(1, "a"), "and no second copy lingers");
        Assert.Equal(1, q.Snapshot().Count, "only the other file is left");
    }

    [Test] void Manifest_ItemById_tracks_direct_list_mutations()
    {
        var m = new LibraryManifest();
        Assert.Null(m.ItemById(1), "empty manifest");
        var a = new LibraryItem { GogId = 1, Title = "A" };
        var b = new LibraryItem { GogId = 2, Title = "B" };
        m.Items.Add(a); m.Items.Add(b);
        Assert.True(ReferenceEquals(a, m.ItemById(1)), "Add is seen (Count moved)");
        Assert.True(ReferenceEquals(b, m.ItemById(2)), "second too");

        m.Items.RemoveAll(i => i.GogId == 1);
        Assert.Null(m.ItemById(1), "RemoveAll is seen");
        Assert.True(ReferenceEquals(b, m.ItemById(2)), "the survivor still resolves");

        // Same-Count replacement: the case Count cannot show. ItemsChanged is the hook; a miss also self-heals.
        var c = new LibraryItem { GogId = 3, Title = "C" };
        m.Items[0] = c;
        m.ItemsChanged();
        Assert.Null(m.ItemById(2), "the replaced item is gone");
        Assert.True(ReferenceEquals(c, m.ItemById(3)), "the replacement resolves");
        var d = new LibraryItem { GogId = 4, Title = "D" };
        m.Items[0] = d;   // no ItemsChanged: the miss path must still find it
        Assert.True(ReferenceEquals(d, m.ItemById(4)), "an unflagged replacement is found by the fallback scan");

        m.Items.Clear();
        Assert.Null(m.ItemById(4), "Clear is seen");
        m.Items = new List<LibraryItem> { a };
        Assert.True(ReferenceEquals(a, m.ItemById(1)), "the loader's setter resets the cache");
    }

    // Count cannot see a remove followed by an add; the Core mutators flag it so a removed item never resolves.
    [Test] void Manifest_ItemById_forgets_an_item_the_Core_mutators_removed_when_Count_did_not_move()
    {
        var m = new LibraryManifest();
        m.Items.Add(new LibraryItem { GogId = 1, Title = "A" });
        m.Items.Add(new LibraryItem { GogId = 2, Title = "B" });
        Assert.True(m.ItemById(1) is not null, "cache built with 1 in it");

        Assert.Equal(1, Grog.Core.Sync.LibraryUntrack.RemoveItems(m, new[] { 1L }), "one removed");
        var c = new LibraryItem { GogId = 3, Title = "C" };
        m.Items.Add(c);   // Count is back where it was
        Assert.Null(m.ItemById(1), "the removed item does not resolve");
        Assert.True(ReferenceEquals(c, m.ItemById(3)), "the added one does");
    }
}
