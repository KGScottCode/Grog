// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System.Collections.Generic;
using System.Linq;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Sync;
using Grog.Core.Tests.Framework;

/// <summary>
/// Untracking (Grog_Design_TOC: remove = stop tracking, bytes stay). Rules locked down here:
/// removing an owner strips per-file stamps and only SOLE-owned files/items leave; co-owned games
/// survive with the other owners intact; removed items take their queue entries with them; the
/// fresh-library reset empties items + queue and nothing else; legacy AccountId attribution is
/// blanked so load-time seeding cannot resurrect removed ownership.
/// </summary>
[NewBatch]
[Trait("untrack")]
public class LibraryUntrackTests
{
    private static LibraryItem Game(long id, params (string Key, string[] Owners)[] files)
    {
        var item = new LibraryItem { GogId = id, Title = $"Game {id}", Slug = $"game-{id}" };
        foreach (var (key, owners) in files)
            item.Files.Add(new GameFile { GameGogId = id, FileKey = key, Name = key, OwnerIds = owners.ToList() });
        return item;
    }

    private static LibraryManifest Manifest(params LibraryItem[] items)
    {
        var m = new LibraryManifest();
        m.Items.AddRange(items);
        return m;
    }

    [Test]
    public void Sole_owned_preview_names_only_single_owner_items()
    {
        var m = Manifest(
            Game(1, ("a", new[] { "alice" })),
            Game(2, ("a", new[] { "alice", "bob" })),
            Game(3, ("a", new[] { "bob" })));

        var sole = LibraryUntrack.SoleOwnedBy(m, "alice");

        Assert.True(sole.Select(i => i.GogId).SequenceEqual(new long[] { 1 }),
            "only the game alice alone owns is previewed as leaving");
    }

    [Test]
    public void Removing_an_owner_removes_sole_owned_items_and_keeps_co_owned()
    {
        var m = Manifest(
            Game(1, ("a", new[] { "alice" }), ("b", new[] { "alice" })),
            Game(2, ("a", new[] { "alice", "bob" })),
            Game(3, ("a", new[] { "bob" })));
        m.Downloads.Enqueue(1, "a");
        m.Downloads.Enqueue(2, "a");

        var removed = LibraryUntrack.RemoveOwner(m, "alice");

        Assert.True(removed.Select(i => i.GogId).SequenceEqual(new long[] { 1 }), "sole-owned game left");
        Assert.True(m.Items.Select(i => i.GogId).SequenceEqual(new long[] { 2, 3 }), "co-owned + unrelated stay");
        Assert.True(m.Items[0].Files[0].OwnerIds.SequenceEqual(new[] { "bob" }), "alice's stamp stripped");
        Assert.True(m.Downloads.Snapshot().Select(q => q.GogId).SequenceEqual(new long[] { 2 }),
            "the removed game's queue entry purged; the survivor's kept");
    }

    [Test]
    public void A_sole_owned_file_inside_a_co_owned_game_leaves_with_its_owner()
    {
        var m = Manifest(Game(1, ("shared", new[] { "alice", "bob" }), ("regional", new[] { "alice" })));

        var removed = LibraryUntrack.RemoveOwner(m, "alice");

        Assert.Equal(0, removed.Count, "the game survives - bob still owns a file");
        Assert.Equal(1, m.Items[0].Files.Count, "alice's regional file no longer tracked");
        Assert.Equal("shared", m.Items[0].Files[0].FileKey);
    }

    [Test]
    public void Legacy_AccountId_attribution_is_blanked_so_seeding_cannot_resurrect_ownership()
    {
        var g = Game(1, ("shared", new[] { "alice", "bob" }));
        g.AccountId = "alice";
        var m = Manifest(g);

        LibraryUntrack.RemoveOwner(m, "alice");

        Assert.Equal("", m.Items[0].AccountId, "attribution blanked");
        Assert.True(m.Items[0].OwnerIds.SequenceEqual(new[] { "bob" }), "derived owners exclude alice");
    }

    [Test]
    public void RemoveItems_removes_named_items_and_their_queue_entries_only()
    {
        var m = Manifest(
            Game(1, ("a", new[] { "alice" })),
            Game(2, ("a", new[] { "alice" })));
        m.Downloads.Enqueue(1, "a");
        m.Downloads.Enqueue(2, "a");

        int n = LibraryUntrack.RemoveItems(m, new long[] { 1 });

        Assert.Equal(1, n);
        Assert.True(m.Items.Select(i => i.GogId).SequenceEqual(new long[] { 2 }));
        Assert.True(m.Downloads.Snapshot().Select(q => q.GogId).SequenceEqual(new long[] { 2 }));
    }

    [Test]
    public void RemoveItems_marks_every_root_holding_the_removed_bytes_for_reimport()
    {
        var m = Manifest(Game(1, ("a", new[] { "alice" }), ("b", new[] { "alice" })), Game(2, ("a", new[] { "alice" })));
        var primary = new BackupRoot { Id = "primary" }; var second = new BackupRoot { Id = "second" }; var idle = new BackupRoot { Id = "idle" };
        m.Roots.AddRange(new[] { primary, second, idle }); m.PrimaryRootId = "primary";
        var one = m.Items[0];
        one.Files[0].State = FileState.Verified; one.Files[0].LocalRelativePath = "Games/one/a";            // null RootId: the primary
        one.Files[1].State = FileState.Verified; one.Files[1].LocalRelativePath = "Games/one/b"; one.Files[1].RootId = "second";
        m.Items[1].Files[0].State = FileState.Verified; m.Items[1].Files[0].LocalRelativePath = "Games/two/a"; m.Items[1].Files[0].RootId = "idle";

        LibraryUntrack.RemoveItems(m, new long[] { 1 });

        Assert.True(primary.PendingImport, "the primary held the removed file with no RootId");
        Assert.True(second.PendingImport, "the second drive held the other removed file");
        Assert.False(idle.PendingImport, "a drive holding only a game that stays owes nothing");
    }

    [Test]
    public void Fresh_library_reset_empties_items_and_queue_and_keeps_accounts()
    {
        var m = Manifest(Game(1, ("a", new[] { "alice" })), Game(2, ("a", new[] { "alice" })));
        m.Accounts.Add(new GrogAccount { Id = "alice", Username = "alice", LastSync = System.DateTimeOffset.UtcNow });
        m.Downloads.Enqueue(1, "a");
        m.Downloads.Paused = true;
        m.LastSyncCompleted = System.DateTimeOffset.UtcNow;
        m.SyncLog.Add(new SyncLogEntry());
        m.Excluded.Add(new ExcludedProduct { GogId = 9 });
        m.CloudCollapsedAccounts.Add("alice");
        m.Roots.Add(new Grog.Core.Models.BackupRoot { Id = "r1", Label = "Primary", PathHint = "/x" });

        int n = LibraryUntrack.RemoveAllItems(m);

        Assert.True(m.Roots[0].PendingImport, "every drive owes an import again: its bytes are still there (sweep 2 #12)");

        Assert.Equal(2, n);
        Assert.Equal(0, m.Items.Count);
        Assert.True(m.Downloads.IsEmpty, "queue emptied");
        Assert.False(m.Downloads.Paused, "paused flag cleared with the queue");
        Assert.Equal(1, m.Accounts.Count, "accounts stay - only items leave");
        Assert.True(m.LastSyncCompleted is null,
            "sync stamp cleared - 'synced recently' must not skip the rebuild scan");
        Assert.Equal(0, m.SyncLog.Count, "scan history is library-derived - gone");
        Assert.Equal(0, m.Excluded.Count, "excluded products are last-scan residue - gone");
        Assert.Equal(0, m.CloudCollapsedAccounts.Count, "cloud view state names gone sections - gone");
        Assert.True(m.Accounts[0].LastSync is null, "per-account sync stamp cleared");
    }

    // Sweep 2 #31: the removed account's saves resurfaced under the first remaining account.
    [Test]
    public void Removing_an_account_takes_its_cloud_mirror_so_nothing_is_reseeded_under_another()
    {
        var m = Manifest(Game(1, ("a", new[] { "alice", "bob" })));
        m.Accounts.Add(new GrogAccount { Id = "bob", Username = "bob" });
        var item = m.Items[0];
        item.CloudByAccount.Add(new CloudAccountSave { AccountId = "alice", SizeBytes = 9, Files = 2 });
        Grog.Core.CloudSaves.CloudSaveReconciler.MirrorLegacy(item);
        Assert.True(item.HasCloudSaves ?? false, "mirrored from alice's entry");

        LibraryUntrack.RemoveOwner(m, "alice");
        Grog.Core.CloudSaves.CloudSaveReconciler.SeedLegacy(m);   // what the next reconcile does first

        Assert.Equal(0, item.CloudByAccount.Count, "alice's saves are not bob's");
        Assert.False(item.HasCloudSaves ?? false, "the mirror went with the entry");
    }

    [Test]
    public void A_file_less_item_attributed_only_to_the_account_leaves_with_it()
    {
        // A pack/DLC shell has no files; its ownership is the legacy item-level attribution. SoleOwnedBy
        // previews it, so RemoveOwner must remove it too or the dialog's count lies.
        var shell = new LibraryItem { GogId = 9, Title = "Shell", AccountId = "a" };
        var kept = Game(1, ("k", new[] { "a", "b" }));
        var m = Manifest(shell, kept);
        var removed = LibraryUntrack.RemoveOwner(m, "a");
        Assert.True(removed.Any(i => i.GogId == 9), "the attributed shell left");
        Assert.True(m.ItemById(1) is not null, "the co-owned game stayed");
        Assert.Equal("", m.ItemById(1)!.AccountId, "legacy attribution blanked where it named the account");
    }

    [Test]
    public void Cloud_save_state_for_the_removed_account_is_pruned()
    {
        var g = Game(1, ("k", new[] { "a", "b" }));
        g.CloudByAccount.Add(new CloudAccountSave { AccountId = "a", Files = 2 });
        g.CloudByAccount.Add(new CloudAccountSave { AccountId = "b", Files = 1 });
        var m = Manifest(g);
        LibraryUntrack.RemoveOwner(m, "a");
        Assert.Equal(1, g.CloudByAccount.Count, "a dangling cloud row would draw a Cloud Saves entry for a gone account");
        Assert.Equal("b", g.CloudByAccount[0].AccountId, "the other account's row stays");
    }
}
