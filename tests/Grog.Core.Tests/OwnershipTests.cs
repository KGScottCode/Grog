// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Storage;
using Grog.Core.Sync;
using Grog.Core.Tests.Framework;

/// <summary>
/// Per-file owners (Grog_MultiAccount_Design.md). One shared library; accounts are overlapping views.
/// The rules locked down here: a second account scanning an owned game ADDS itself to the files' owners
/// (no duplicate record, no re-queue), attribution is first-sync and never stolen, a regional file owned
/// by one account carries only that owner, and a pre-owners manifest seeds owners from AccountId on load.
/// </summary>
[NewBatch]
[Trait("owners")]
public class OwnershipTests
{
    private static LibraryItem Game(long id, string accountId, params (string Key, string[] Owners)[] files)
    {
        var item = new LibraryItem { GogId = id, Title = $"Game {id}", Slug = $"game-{id}", AccountId = accountId };
        foreach (var (key, owners) in files)
            item.Files.Add(new GameFile { GameGogId = id, FileKey = key, Name = key, OwnerIds = owners.ToList() });
        return item;
    }

    private static LibraryItem Incoming(long id, params string[] keys)
    {
        var item = new LibraryItem { GogId = id, Title = $"Game {id}", Slug = $"game-{id}" };
        foreach (var k in keys)
            item.Files.Add(new GameFile { GameGogId = id, FileKey = k, Name = k });
        return item;
    }

    [Test]
    public void Second_account_scanning_an_owned_game_adds_owner_without_duplicating()
    {
        var existing = Game(1, "", ("setup.exe", new[] { "" }), ("manual.pdf", new[] { "" }));
        existing.Files[0].State = FileState.Verified;
        var incoming = Incoming(1, "setup.exe", "manual.pdf");

        LibrarySyncService.MergeIntoForTests(existing, incoming, accountId: "kids");

        Assert.Equal(2, existing.Files.Count, "no duplicate file records");
        Assert.True(existing.Files.All(f => f.OwnerIds.SequenceEqual(new[] { "", "kids" })),
            "both files now list both owners, primary first");
        Assert.Equal(FileState.Verified, existing.Files[0].State,
            "an already-held file keeps its state - nothing to re-queue");
        Assert.True(existing.OwnerIds.SequenceEqual(new[] { "", "kids" }), "the game's owners are the union");
    }

    [Test]
    public void Rescanning_the_same_account_does_not_double_the_owner()
    {
        var existing = Game(1, "", ("setup.exe", new[] { "" }));
        LibrarySyncService.MergeIntoForTests(existing, Incoming(1, "setup.exe"), accountId: "");
        Assert.True(existing.Files[0].OwnerIds.SequenceEqual(new[] { "" }), "owner listed once");
    }

    [Test]
    public void A_file_offered_to_one_account_only_carries_only_that_owner()
    {
        // The regional extra: kids' scan returns a bonus file kevin's never did. It joins the game with
        // kids as its ONLY owner, while the shared file gains both.
        var existing = Game(1, "", ("setup.exe", new[] { "" }));
        var incoming = Incoming(1, "setup.exe", "regional-bonus.zip");

        LibrarySyncService.MergeIntoForTests(existing, incoming, accountId: "kids");

        var bonus = existing.Files.Single(f => f.FileKey == "regional-bonus.zip");
        Assert.True(bonus.OwnerIds.SequenceEqual(new[] { "kids" }), "the regional file is kids-only");
        var shared = existing.Files.Single(f => f.FileKey == "setup.exe");
        Assert.True(shared.OwnerIds.SequenceEqual(new[] { "", "kids" }), "the shared file is co-owned");
        Assert.True(existing.OwnerIds.SequenceEqual(new[] { "", "kids" }), "game union covers both");
    }

    [Test]
    public async Task A_pre_owners_manifest_seeds_owners_from_AccountId_on_load_and_round_trips()
    {
        var root = Directory.CreateTempSubdirectory("grog-own-").FullName;
        Environment.SetEnvironmentVariable("GROG_CONFIG_DIR", Path.Combine(root, "_cfg"));
        try
        {
            var paths = GrogPaths.Resolve(root);
            var store = new JsonManifestStore(paths);
            await store.LoadAsync();
            var item = Game(7, "kids", ("setup.exe", Array.Empty<string>()));
            item.OldVersionFiles.Add(new GameFile { GameGogId = 7, FileKey = "old-setup.exe", IsOldVersion = true });
            store.Current.Items.Add(item);
            await store.SaveAsync();

            var store2 = new JsonManifestStore(paths);
            await store2.LoadAsync();
            var back = store2.Current.Items.Single(i => i.GogId == 7);
            Assert.True(back.Files[0].OwnerIds.SequenceEqual(new[] { "kids" }),
                "an ownerless file is seeded from the item's AccountId at load");
            Assert.True(back.OldVersionFiles[0].OwnerIds.SequenceEqual(new[] { "kids" }),
                "Old Versions entries are seeded too");

            // And a file that HAS owners round-trips them untouched.
            back.Files[0].OwnerIds.Add("");
            await store2.SaveAsync();
            var store3 = new JsonManifestStore(paths);
            await store3.LoadAsync();
            Assert.True(store3.Current.Items.Single(i => i.GogId == 7).Files[0].OwnerIds
                    .SequenceEqual(new[] { "kids", "" }), "persisted owners survive the round trip");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Test]
    public void Game_union_falls_back_to_attribution_before_migration_touches_it()
    {
        var item = Game(3, "kids", ("setup.exe", Array.Empty<string>()));
        Assert.True(item.OwnerIds.SequenceEqual(new[] { "kids" }),
            "with no per-file owners yet, attribution still answers as the owner");
    }
}
