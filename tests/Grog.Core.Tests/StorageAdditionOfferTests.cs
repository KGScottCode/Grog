// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System.Linq;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Tests.Framework;
using Grog.Core.Volumes;

namespace Grog.Core.Tests;

/// <summary>(09-13) The numbers behind the "new storage added" offer. Pure manifest arithmetic; the App only
/// renders these and dispatches the chosen move.</summary>
[Trait("placement")]
public sealed class StorageAdditionOfferTests
{
    private static GameFile F(long game, string key, FileKind kind = FileKind.Installer, FileState state = FileState.NotBackedUp,
                              string? root = null, long size = 10)
        => new() { GameGogId = game, FileKey = key, Name = key, Kind = kind, State = state, RootId = root,
                   LocalRelativePath = state is FileState.Verified or FileState.Present ? $"g/{key}" : null,
                   ExpectedSizeBytes = size, LocalSizeBytes = state is FileState.Verified or FileState.Present ? size : null };

    private static LibraryManifest Lib()
    {
        var m = new LibraryManifest { PrimaryRootId = "p" };
        m.Roots.Add(new BackupRoot { Id = "p", Label = "Primary" });
        m.Roots.Add(new BackupRoot { Id = "s", Label = "Secondary" });
        // Split: one present on p, one queued to s.
        m.Items.Add(new LibraryItem { GogId = 1, Title = "Split", Slug = "split", Files = { F(1, "a", state: FileState.Verified, root: "p"), F(1, "b", size: 100) } });
        // Whole on p: nothing queued.
        m.Items.Add(new LibraryItem { GogId = 2, Title = "Whole", Slug = "whole", Files = { F(2, "c", state: FileState.Verified) } });   // null RootId = primary
        // Only queued: nothing present yet.
        m.Items.Add(new LibraryItem { GogId = 3, Title = "Fresh", Slug = "fresh", Files = { F(3, "d", size: 50) } });
        // Split with a pinned extra on p.
        m.Items.Add(new LibraryItem { GogId = 4, Title = "Extras", Slug = "extras", Files = { F(4, "e", FileKind.Extra, FileState.Verified, "p", 7), F(4, "f", size: 20) } });
        m.Items[0].OldVersionFiles.Add(F(1, "a#old:1", state: FileState.Verified, root: "p", size: 3));
        m.Downloads.Enqueue(1, "b", "s", fits: true);
        m.Downloads.Enqueue(3, "d", "s", fits: true);
        m.Downloads.Enqueue(4, "f", "s", fits: true);
        return m;
    }

    [Test]
    public void ShouldOffer_only_when_a_queued_row_targets_the_new_root()
    {
        var m = Lib();
        Assert.True(StorageAdditionOffer.ShouldOffer(m, "s"), "rows target s");
        Assert.False(StorageAdditionOffer.ShouldOffer(m, "zzz"), "nothing targets zzz");
        foreach (var q in m.Downloads.Snapshot()) m.Downloads.SetPlacement(q.GogId, q.FileKey, "s", fits: false);
        Assert.False(StorageAdditionOffer.ShouldOffer(m, "s"), "won't-fit rows do not count");
    }

    [Test]
    public void Counts_queue_to_new_and_files_on_old_primary_including_old_versions()
    {
        var m = Lib();
        m.Items[2].Files[0].HasPartial = true; m.Items[2].Files[0].PartialBytes = 20;   // 50 needs 30
        var r = StorageAdditionOffer.Compute(m, "s", "p");
        Assert.Equal(3, r.QueuedToNewCount, "b, d, f");
        Assert.Equal(100L + 30L + 20L, r.QueuedToNewBytes, "priced at what is still needed");
        Assert.Equal(4, r.PrimaryFileCount, "a, a#old, c (null RootId = primary), e");
        Assert.Equal(10L + 3L + 10L + 7L, r.PrimaryBytes);
    }

    [Test]
    public void Split_games_are_those_with_files_on_primary_AND_files_queued_to_new()
    {
        var r = StorageAdditionOffer.Compute(Lib(), "s", "p");
        Assert.Equal(2, r.SplitGameCount, "Split and Extras; Whole has no queue row, Fresh has nothing present");
        var keys = string.Join(",", r.SplitGameFiles.Select(x => x.File.FileKey).OrderBy(k => k));
        Assert.Equal("a,a#old:1,e", keys, "present files of the split games, archive included, extra included (WithGame: no pin)");
        Assert.Equal(0, r.ExtrasHeldBackByPin);
    }

    [Test]
    public void A_pinned_extra_is_held_back_from_the_per_game_move_but_not_from_move_everything()
    {
        var m = Lib();
        m.SetExtrasLayout(ExtrasPlacement.SeparateByGame);
        m.Routing.SetRole(ContentRole.Extras, "p");
        var r = StorageAdditionOffer.Compute(m, "s", "p");
        Assert.True(r.SplitGameFiles.All(x => x.File.FileKey != "e"), "the pinned extra stays where the setting put it");
        Assert.Equal(1, r.ExtrasHeldBackByPin);
        Assert.True(r.AllPrimaryFiles.Any(x => x.File.FileKey == "e"), "move-everything carries it (the pin follows the drive)");
    }

    [Test]
    public void RetargetRoles_moves_every_role_from_old_to_new()
    {
        var m = Lib();
        m.Routing.SetRole(ContentRole.Extras, "p"); m.Routing.SetRole(ContentRole.Games, "other");
        var store = new MemStore(m);
        int n = new VolumeService(store).RetargetRoles("p", "s");
        Assert.Equal(1, n);
        Assert.Equal("s", m.Routing.RoleRoots[ContentRole.Extras]); Assert.Equal("other", m.Routing.RoleRoots[ContentRole.Games], "untouched");
    }

    private sealed class MemStore : IManifestStore
    {
        public MemStore(LibraryManifest m) => Current = m;
        public LibraryManifest Current { get; }
        public ManifestGate Gate { get; } = new();
        public System.Threading.Tasks.Task LoadAsync(System.Threading.CancellationToken ct = default) => System.Threading.Tasks.Task.CompletedTask;
        public System.Threading.Tasks.Task SaveAsync(System.Threading.CancellationToken ct = default) => System.Threading.Tasks.Task.CompletedTask;
    }
}
