// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using Grog.Core.CloudSaves;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Tests.Framework;

/// <summary>
/// Per-account cloud saves (multi-account design: saves are per-account facts, never merged). Locked
/// down: a scanned account's entries replace only ITS OWN, an unscanned account keeps its section, the
/// legacy single-slot fields mirror the first entry in registration order, and pre-account manifests
/// seed their old flag under the tokens.json slot.
/// </summary>
[NewBatch]
[Trait("accounts")]
public sealed class CloudAccountReconcilerTests
{
    private static CloudContainer C(long pid, long size = 10, int files = 1)
        => new(pid, size, files, $"space-{pid}", 0, DateTimeOffset.UnixEpoch);

    private static LibraryManifest Manifest(params LibraryItem[] items)
    {
        var m = new LibraryManifest();
        m.Accounts.Add(new GrogAccount { Id = "kevin", Username = "kevin" });
        m.Accounts.Add(new GrogAccount { Id = "kg", Username = "kgscott" });
        m.Items.AddRange(items);
        return m;
    }

    [Test]
    public void Scanned_account_replaces_only_its_own_entries()
    {
        var game = new LibraryItem { GogId = 1, Title = "Doom" };
        game.CloudByAccount.Add(new CloudAccountSave { AccountId = "kevin", SizeBytes = 5 });
        game.CloudByAccount.Add(new CloudAccountSave { AccountId = "kg", SizeBytes = 7 });
        var m = Manifest(game);

        // Only kg scanned, and its container grew; kevin's section must be untouched.
        CloudSaveReconciler.ApplyAccounts(m, new List<(string, IReadOnlyList<CloudContainer>)>
            { ("kg", new[] { C(1, size: 99) }) });

        Assert.Equal(2, game.CloudByAccount.Count, "both sections survive");
        Assert.Equal(5, game.CloudByAccount.Single(e => e.AccountId == "kevin").SizeBytes, "unscanned account kept as last seen");
        Assert.Equal(99, game.CloudByAccount.Single(e => e.AccountId == "kg").SizeBytes, "scanned account replaced");
    }

    [Test]
    public void Scanned_account_with_no_container_leaves_the_game_and_mirror_follows_first_entry()
    {
        var game = new LibraryItem { GogId = 1, Title = "Doom" };
        game.CloudByAccount.Add(new CloudAccountSave { AccountId = "kevin", SizeBytes = 5 });
        game.CloudByAccount.Add(new CloudAccountSave { AccountId = "kg", SizeBytes = 7 });
        var m = Manifest(game);

        // kevin scanned and no longer has saves for the game: his entry leaves, kg's remains,
        // and the legacy mirror now reads kg's numbers (first entry in registration order).
        CloudSaveReconciler.ApplyAccounts(m, new List<(string, IReadOnlyList<CloudContainer>)>
            { ("kevin", Array.Empty<CloudContainer>()) });

        Assert.Equal("kg", game.CloudByAccount.Single().AccountId, "only kg's section remains");
        Assert.True(game.HasCloudSaves == true, "still a cloud game");
        Assert.Equal(7, game.CloudSaveSize, "mirror follows the surviving first entry");
    }

    [Test]
    public void No_entries_left_clears_the_flag_and_cached_identity()
    {
        var game = new LibraryItem { GogId = 1, Title = "Doom", CloudClientId = "stale", CloudSpaceId = "s" };
        game.CloudByAccount.Add(new CloudAccountSave { AccountId = "kevin", SizeBytes = 5, SpaceId = "s" });
        var m = Manifest(game);

        CloudSaveReconciler.ApplyAccounts(m, new List<(string, IReadOnlyList<CloudContainer>)>
            { ("kevin", Array.Empty<CloudContainer>()), ("kg", Array.Empty<CloudContainer>()) });

        Assert.True(game.HasCloudSaves == false, "not a cloud game any more");
        Assert.Equal("", game.CloudClientId, "stale cloud identity dropped");
        Assert.Equal(0, game.CloudByAccount.Count, "no sections");
    }

    [Test]
    public void Legacy_single_slot_manifest_seeds_under_the_first_account()
    {
        var game = new LibraryItem { GogId = 1, Title = "Doom", HasCloudSaves = true, CloudSaveSize = 42, CloudSaveFiles = 3,
                                     CloudLocalSaveCount = 2, CloudBackedUpSize = 40 };
        var m = Manifest(game);

        CloudSaveReconciler.SeedLegacy(m);

        var e = game.CloudByAccount.Single();
        Assert.Equal("kevin", e.AccountId, "seeded under the account that discovered it");
        Assert.Equal(42, e.SizeBytes, "legacy numbers carried over");
        Assert.Equal(2, e.LocalSaveCount, "the legacy archive's bookkeeping moves in too");
        Assert.Equal(40, e.BackedUpSize, "including the backed-up fingerprint");
    }

    [Test]
    public void Pruning_local_saves_updates_the_accounts_entry_and_the_mirror_together()
    {
        // The single-write-path rule: bookkeeping lands in the per-account entry, MirrorLegacy follows.
        // Deleting old saves keeps the fingerprint while any save remains; emptying the history clears it,
        // so the game honestly reads "not backed up" again.
        var game = new LibraryItem { GogId = 1, Title = "Doom" };
        game.CloudByAccount.Add(new CloudAccountSave
            { AccountId = "kevin", LocalSaveCount = 3, BackedUpSize = 40, BackedUpFiles = 4, LastBackup = DateTimeOffset.UnixEpoch });
        Manifest(game);

        var newest = DateTimeOffset.UnixEpoch.AddDays(1);
        CloudSaveReconciler.RecordLocalSaves(game, "kevin", 2, newest);
        Assert.Equal(2, game.CloudByAccount.Single().LocalSaveCount, "entry updated");
        Assert.Equal(40, game.CloudByAccount.Single().BackedUpSize, "fingerprint kept while saves remain");
        Assert.Equal(2, game.CloudLocalSaveCount, "mirror follows the entry");
        Assert.Equal(newest, game.CloudLastBackup, "including the newest stamp");

        CloudSaveReconciler.RecordLocalSaves(game, "kevin", 0, null);
        Assert.Equal(0, game.CloudByAccount.Single().BackedUpSize, "emptied history clears the fingerprint");
        Assert.Null(game.CloudLastBackup, "and the mirror agrees");
    }

    [Test]
    public void Rescan_replaces_server_facts_but_keeps_local_backup_bookkeeping()
    {
        // A listing call changes nothing on disk: the archive and its fingerprint must survive the
        // per-account replacement, or every rescan would flip backed-up games to "never backed up".
        var game = new LibraryItem { GogId = 1, Title = "Doom" };
        game.CloudByAccount.Add(new CloudAccountSave
            { AccountId = "kg", SizeBytes = 7, LocalSaveCount = 3, BackedUpSize = 7, LastBackup = DateTimeOffset.UnixEpoch });
        var m = Manifest(game);

        CloudSaveReconciler.ApplyAccounts(m, new List<(string, IReadOnlyList<CloudContainer>)>
            { ("kg", new[] { C(1, size: 99) }) });

        var e = game.CloudByAccount.Single();
        Assert.Equal(99, e.SizeBytes, "server facts replaced");
        Assert.Equal(3, e.LocalSaveCount, "local save count survives the rescan");
        Assert.Equal(7, e.BackedUpSize, "backed-up fingerprint survives (now reads update available)");
        Assert.Equal(DateTimeOffset.UnixEpoch, e.LastBackup, "last-backup stamp survives");
    }

    [Test]
    public void Account_archive_dirs_are_named_for_the_account_and_never_collide()
    {
        var m = Manifest();
        Assert.True(CloudSaveReconciler.AccountArchiveDir(m, "base", "kevin").EndsWith("kevin"),
            "every account's folder is its (lowercased) username, the tokens.json slot included");
        Assert.True(CloudSaveReconciler.AccountArchiveDir(m, "base", "kg").EndsWith("kgscott"), "named account likewise");
    }

    [Test]
    public void A_per_account_folder_override_fully_replaces_the_default_and_skips_the_legacy_fallback()
    {
        var m = Manifest();
        m.Accounts.Single(a => a.Id == "kevin").CloudSavesFolder = "X:\\my-saves";
        Assert.Equal("X:\\my-saves", CloudSaveReconciler.AccountArchiveDir(m, "base", "kevin"), "override wins over base/<name>");
        Assert.True(CloudSaveReconciler.GameArchiveDir(m, "base", "kevin", "doom").StartsWith("X:\\my-saves"),
            "an explicit folder is the answer -- no legacy flat-layout fallback");
    }

    [Test]
    public void Legacy_flat_archive_stays_readable_for_the_first_account()
    {
        // Pre-account installs archived at <base>/<game>/cloud-saves. Those bytes never move: the first
        // slot keeps resolving there while such an archive exists and no per-account one does.
        var m = Manifest();
        var root = System.IO.Directory.CreateTempSubdirectory("grog-cloud-").FullName;
        try
        {
            var legacy = System.IO.Path.Combine(root, "doom", "cloud-saves", "20240101-000000");
            System.IO.Directory.CreateDirectory(legacy);
            System.IO.File.WriteAllText(System.IO.Path.Combine(legacy, "save.dat"), "x");

            Assert.Equal(System.IO.Path.Combine(root, "doom"),
                CloudSaveReconciler.GameArchiveDir(m, root, "kevin", "doom"), "legacy archive read where it is");
            Assert.True(CloudSaveReconciler.GameArchiveDir(m, root, "kevin", "quake").Contains("kevin"),
                "a game with no legacy archive writes under the account folder");
            Assert.True(CloudSaveReconciler.GameArchiveDir(m, root, "kg", "doom").Contains("kgscott"),
                "named accounts never fall back to the flat layout");
        }
        finally { try { System.IO.Directory.Delete(root, true); } catch { } }
    }
}
