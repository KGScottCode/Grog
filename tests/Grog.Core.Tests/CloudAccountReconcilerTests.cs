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

[NewBatch]
[Trait("accounts")]
public sealed class CloudAccountReconcilerTests
{
    private static CloudContainer C(long pid, long size = 10, int files = 1)
        => new(pid, size, files, $"space-{pid}", 0, DateTimeOffset.UnixEpoch);

    private static LibraryManifest Manifest(params LibraryItem[] items)
    {
        var m = new LibraryManifest();
        m.Accounts.Add(new GrogAccount { Id = "acct", Username = "acct" });
        m.Accounts.Add(new GrogAccount { Id = "kg", Username = "acctname" });
        m.Items.AddRange(items);
        return m;
    }

    [Test]
    public void Scanned_account_replaces_only_its_own_entries()
    {
        var game = new LibraryItem { GogId = 1, Title = "Doom" };
        game.CloudByAccount.Add(new CloudAccountSave { AccountId = "acct", SizeBytes = 5 });
        game.CloudByAccount.Add(new CloudAccountSave { AccountId = "kg", SizeBytes = 7 });
        var m = Manifest(game);

        // Only kg scanned, and its container grew; acct's section must be untouched.
        CloudSaveReconciler.ApplyAccounts(m, new List<(string, IReadOnlyList<CloudContainer>)>
            { ("kg", new[] { C(1, size: 99) }) });

        Assert.Equal(2, game.CloudByAccount.Count, "both sections survive");
        Assert.Equal(5, game.CloudByAccount.Single(e => e.AccountId == "acct").SizeBytes, "unscanned account kept as last seen");
        Assert.Equal(99, game.CloudByAccount.Single(e => e.AccountId == "kg").SizeBytes, "scanned account replaced");
    }

    [Test]
    public void NoContainer_KeepsGame_MirrorFollowsFirst()
    {
        var game = new LibraryItem { GogId = 1, Title = "Doom" };
        game.CloudByAccount.Add(new CloudAccountSave { AccountId = "acct", SizeBytes = 5 });
        game.CloudByAccount.Add(new CloudAccountSave { AccountId = "kg", SizeBytes = 7 });
        var m = Manifest(game);

        // acct scanned and no longer has saves for the game: his entry leaves, kg's remains,
        // and the legacy mirror now reads kg's numbers (first entry in registration order).
        CloudSaveReconciler.ApplyAccounts(m, new List<(string, IReadOnlyList<CloudContainer>)>
            { ("acct", Array.Empty<CloudContainer>()) });

        Assert.Equal("kg", game.CloudByAccount.Single().AccountId, "only kg's section remains");
        Assert.True(game.HasCloudSaves == true, "still a cloud game");
        Assert.Equal(7, game.CloudSaveSize, "mirror follows the surviving first entry");
    }

    [Test]
    public void NoEntriesLeft_ClearsFlagAndIdentity()
    {
        var game = new LibraryItem { GogId = 1, Title = "Doom", CloudClientId = "stale", CloudSpaceId = "s" };
        game.CloudByAccount.Add(new CloudAccountSave { AccountId = "acct", SizeBytes = 5, SpaceId = "s" });
        var m = Manifest(game);

        CloudSaveReconciler.ApplyAccounts(m, new List<(string, IReadOnlyList<CloudContainer>)>
            { ("acct", Array.Empty<CloudContainer>()), ("kg", Array.Empty<CloudContainer>()) });

        Assert.True(game.HasCloudSaves == false, "not a cloud game any more");
        Assert.Equal("", game.CloudClientId, "stale cloud identity dropped");
        Assert.Equal(0, game.CloudByAccount.Count, "no sections");
    }

    [Test]
    public void LegacySingleSlot_SeedsUnderFirstAccount()
    {
        var game = new LibraryItem { GogId = 1, Title = "Doom", HasCloudSaves = true, CloudSaveSize = 42, CloudSaveFiles = 3,
                                     CloudLocalSaveCount = 2, CloudBackedUpSize = 40 };
        var m = Manifest(game);

        CloudSaveReconciler.SeedLegacy(m);

        var e = game.CloudByAccount.Single();
        Assert.Equal("acct", e.AccountId, "seeded under the account that discovered it");
        Assert.Equal(42, e.SizeBytes, "legacy numbers carried over");
        Assert.Equal(2, e.LocalSaveCount, "the legacy archive's bookkeeping moves in too");
        Assert.Equal(40, e.BackedUpSize, "including the backed-up fingerprint");
    }

    [Test]
    public void PruneLocalSaves_UpdatesEntryAndMirror()
    {
        // The single-write-path rule: bookkeeping lands in the per-account entry, MirrorLegacy follows.
        // Deleting old saves keeps the fingerprint while any save remains; emptying the history clears it,
        // so the game honestly reads "not backed up" again.
        var game = new LibraryItem { GogId = 1, Title = "Doom" };
        game.CloudByAccount.Add(new CloudAccountSave
            { AccountId = "acct", LocalSaveCount = 3, BackedUpSize = 40, BackedUpFiles = 4, LastBackup = DateTimeOffset.UnixEpoch });
        Manifest(game);

        var newest = DateTimeOffset.UnixEpoch.AddDays(1);
        CloudSaveReconciler.RecordLocalSaves(game, "acct", 2, newest);
        Assert.Equal(2, game.CloudByAccount.Single().LocalSaveCount, "entry updated");
        Assert.Equal(40, game.CloudByAccount.Single().BackedUpSize, "fingerprint kept while saves remain");
        Assert.Equal(2, game.CloudLocalSaveCount, "mirror follows the entry");
        Assert.Equal(newest, game.CloudLastBackup, "including the newest stamp");

        CloudSaveReconciler.RecordLocalSaves(game, "acct", 0, null);
        Assert.Equal(0, game.CloudByAccount.Single().BackedUpSize, "emptied history clears the fingerprint");
        Assert.Null(game.CloudLastBackup, "and the mirror agrees");
    }

    [Test]
    public void Rescan_ReplacesServerFacts_KeepsLocal()
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
    public void ArchiveDirs_NamedPerAccount_NeverCollide()
    {
        var m = Manifest();
        Assert.True(CloudSaveReconciler.AccountArchiveDir(m, "base", "acct").EndsWith("acct"),
            "every account's folder is its (lowercased) username, the tokens.json slot included");
        Assert.True(CloudSaveReconciler.AccountArchiveDir(m, "base", "kg").EndsWith("acctname"), "named account likewise");
    }

    [Test]
    public void FolderOverride_ReplacesDefault_NoLegacy()
    {
        var m = Manifest();
        m.Accounts.Single(a => a.Id == "acct").CloudSavesFolder = "X:\\my-saves";
        Assert.Equal("X:\\my-saves", CloudSaveReconciler.AccountArchiveDir(m, "base", "acct"), "override wins over base/<name>");
        Assert.True(CloudSaveReconciler.GameArchiveDir(m, "base", "acct", "doom").StartsWith("X:\\my-saves"),
            "an explicit folder is the answer -- no legacy flat-layout fallback");
    }

    [Test]
    public void LegacyFlatArchive_FirstAccountReadsIt()
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
                CloudSaveReconciler.GameArchiveDir(m, root, "acct", "doom"), "legacy archive read where it is");
            Assert.True(CloudSaveReconciler.GameArchiveDir(m, root, "acct", "quake").Contains("acct"),
                "a game with no legacy archive writes under the account folder");
            Assert.True(CloudSaveReconciler.GameArchiveDir(m, root, "kg", "doom").Contains("acctname"),
                "named accounts never fall back to the flat layout");
        }
        finally { try { System.IO.Directory.Delete(root, true); } catch { } }
    }
}
