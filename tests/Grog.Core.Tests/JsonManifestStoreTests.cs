// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Tests.Framework;

public class JsonManifestStoreTests
{
    string _dir = "";

    [Setup] void Setup() => _dir = Directory.CreateTempSubdirectory("grog-tests-").FullName;
    [Teardown] void Teardown() => Directory.Delete(_dir, recursive: true);

    string ManifestPath => Path.Combine(_dir, JsonManifestStore.FileName);

    [Test]
    async Task Load_WhenNoFileExists_YieldsEmptyManifest()
    {
        var store = new JsonManifestStore(_dir);
        await store.LoadAsync();

        Assert.Empty(store.Current.Items, "fresh store items");
        Assert.Null(store.Current.LastSyncCompleted, "fresh store last-sync");
    }

    [Test]
    async Task SaveThenLoad_RoundTripsGamesAndFiles()
    {
        var store = new JsonManifestStore(_dir);
        store.Current.Items.Add(new LibraryItem
        {
            GogId = 1207658691,
            Title = "Unreal Tournament 2004 ECE",
            Slug = "unreal_tournament_2004_ece",
            Status = BackupStatus.Partial,
            Files =
            {
                new GameFile
                {
                    GameGogId = 1207658691,
                    FileKey = "/downlink/unreal_tournament_2004_ece/en1installer3",
                    Kind = FileKind.Installer,
                    Name = "Setup (Part 1 of 3)",
                    Os = "windows",
                    Language = "English",
                    State = FileState.Verified,
                    ExpectedMd5 = "d41d8cd98f00b204e9800998ecf8427e",
                }
            }
        });
        await store.SaveAsync();

        var reloaded = new JsonManifestStore(_dir);
        await reloaded.LoadAsync();

        var item = Assert.Single(reloaded.Current.Items, "one game round-trips");
        Assert.Equal("Unreal Tournament 2004 ECE", item.Title, "title");
        Assert.Equal(BackupStatus.Partial, item.Status, "status");
        var file = Assert.Single(item.Files, "one file round-trips");
        Assert.Equal(FileState.Verified, file.State, "file state");
        Assert.Equal("d41d8cd98f00b204e9800998ecf8427e", file.ExpectedMd5, "md5");
    }

    [Test]
    async Task OldVersionFiles_RoundTripSeparatelyFromFiles()
    {
        var store = new JsonManifestStore(_dir);
        store.Current.Items.Add(new LibraryItem
        {
            GogId = 42, Title = "Patched Game", Slug = "patched",
            Files = { new GameFile { GameGogId = 42, FileKey = "cur", Kind = FileKind.Installer, State = FileState.Verified } },
            OldVersionFiles = { new GameFile { GameGogId = 42, FileKey = "cur#old:setup.bin", Kind = FileKind.Installer,
                LocalRelativePath = "Old Versions/patched/setup.bin", LocalSizeBytes = 1234, State = FileState.Verified, IsOldVersion = true } },
        });
        await store.SaveAsync();

        var reloaded = new JsonManifestStore(_dir);
        await reloaded.LoadAsync();
        var item = Assert.Single(reloaded.Current.Items, "one game");
        Assert.Single(item.Files, "current files unchanged");
        var old = Assert.Single(item.OldVersionFiles, "one old version round-trips");
        Assert.True(old.IsOldVersion, "flag persists");
        Assert.Equal(1234L, old.LocalSizeBytes ?? 0, "old-version size persists");
    }

    [Test]
    async Task Save_KeepsBackupOfPreviousVersion()
    {
        var store = new JsonManifestStore(_dir);
        store.Current.Items.Add(new LibraryItem { GogId = 1, Title = "First" });
        await store.SaveAsync();

        store.Current.Items[0].Title = "Second";
        await store.SaveAsync();

        var bak = ManifestPath + ".bak";
        Assert.True(File.Exists(bak), ".bak exists after second save");
        Assert.Contains("First", File.ReadAllText(bak), ".bak holds previous version");
        Assert.Contains("Second", File.ReadAllText(ManifestPath), "manifest holds current version");
    }

    [Test]
    async Task Save_RollsThePreviousPrimaryIntoBakByteForByte()
    {
        var store = new JsonManifestStore(_dir);
        store.Current.Items.Add(new LibraryItem { GogId = 1, Title = "First" });
        await store.SaveAsync();
        var before = File.ReadAllBytes(ManifestPath);

        store.Current.Items[0].Title = "Second";
        await store.SaveAsync();

        Assert.True(before.AsSpan().SequenceEqual(File.ReadAllBytes(ManifestPath + ".bak")), "the .bak is the previous primary, unchanged");
        Assert.False(File.Exists(ManifestPath + ".tmp"), "no temp file left behind");
    }

    [Test]
    async Task Load_RecoversWhenACrashLeftNoPrimaryButATmpAndABak()
    {
        var store = new JsonManifestStore(_dir);
        store.Current.Items.Add(new LibraryItem { GogId = 1, Title = "First" });
        await store.SaveAsync();
        store.Current.Items[0].Title = "Second";
        await store.SaveAsync();
        // The moment between the two renames: the new file is complete as .tmp, the old one is the .bak.
        File.Move(ManifestPath, ManifestPath + ".tmp");

        var reloaded = new JsonManifestStore(_dir);
        await reloaded.LoadAsync();
        Assert.Equal("Second", Assert.Single(reloaded.Current.Items).Title, "the completed save is what loads");
        Assert.True(File.Exists(ManifestPath), "the .tmp became the primary");
    }

    [Test]
    async Task Load_RecoversWhenACrashLeftOnlyAParseableTmp()
    {
        var store = new JsonManifestStore(_dir);
        store.Current.Items.Add(new LibraryItem { GogId = 1, Title = "Only" });
        await store.SaveAsync();
        // A first-ever save that died before its rename: no primary, no .bak, a complete .tmp.
        File.Move(ManifestPath, ManifestPath + ".tmp");
        Assert.False(File.Exists(ManifestPath + ".bak"), "no backup exists yet");

        var reloaded = new JsonManifestStore(_dir);
        await reloaded.LoadAsync();
        Assert.Equal("Only", Assert.Single(reloaded.Current.Items).Title, "the completed save is what loads");
        Assert.True(File.Exists(ManifestPath), "the .tmp became the primary");

        await reloaded.SaveAsync();
        Assert.True(File.Exists(ManifestPath), "the next save leaves a primary");
        Assert.False(File.Exists(ManifestPath + ".tmp"), "and no temp behind");
    }

    [Test]
    async Task Load_IgnoresAGarbageTmpWithNoPrimaryAndNoBak()
    {
        File.WriteAllText(ManifestPath + ".tmp", "{ half a");
        var store = new JsonManifestStore(_dir);
        await store.LoadAsync();
        Assert.Equal(0, store.Current.Items.Count, "starts empty");
        Assert.False(File.Exists(ManifestPath), "a torn .tmp is not promoted");
    }

    [Test]
    async Task SaveSoon_CoalescesABurstIntoOneWrite()
    {
        var store = new JsonManifestStore(_dir) { SaveSoonDelay = TimeSpan.FromMilliseconds(100) };
        store.Current.Items.Add(new LibraryItem { GogId = 1, Title = "Burst" });
        for (int i = 0; i < 10; i++) { store.SaveSoon(); await Task.Delay(5); }
        for (int i = 0; i < 40 && store.SaveCount == 0; i++) await Task.Delay(50);
        await Task.Delay(300);   // long enough for any second debounce to have fired
        Assert.Equal(1, store.SaveCount, "ten calls within the window, one write");
        Assert.Contains("Burst", File.ReadAllText(ManifestPath), "and the write landed");
    }

    [Test]
    async Task Save_CapsSyncLogAtOneHundredEntries()
    {
        var store = new JsonManifestStore(_dir);
        for (int i = 0; i < 150; i++)
            store.Current.SyncLog.Add(new SyncLogEntry { StartedAt = DateTimeOffset.UtcNow, GamesSeen = i });
        await store.SaveAsync();

        Assert.Equal(100, store.Current.SyncLog.Count, "sync log capped");
    }

    // A corrupt PRIMARY must not present an empty library: the last-known-good .bak is loaded instead.
    [Test]
    async Task Load_WhenPrimaryCorrupt_RecoversFromBak()
    {
        var store = new JsonManifestStore(_dir);
        store.Current.Items.Add(new LibraryItem { GogId = 1, Title = "First" });
        await store.SaveAsync();
        store.Current.Items[0].Title = "Second";
        await store.SaveAsync();   // primary=Second, .bak=First

        File.WriteAllText(ManifestPath, "{ this is not valid json ]");   // clobber the primary

        var reloaded = new JsonManifestStore(_dir);
        await reloaded.LoadAsync();
        var item = Assert.Single(reloaded.Current.Items, "recovered the game from .bak, not an empty library");
        Assert.Equal("First", item.Title, "recovered content is the last-known-good .bak");
        Assert.True(File.Exists(ManifestPath + ".corrupt"), "corrupt primary quarantined for forensics");
    }

    [Test]
    async Task Load_WhenPrimaryCorrupt_SetsRecoveryNote_NamingTheBackup()
    {
        var store = new JsonManifestStore(_dir);
        store.Current.Items.Add(new LibraryItem { GogId = 1, Title = "First" });
        await store.SaveAsync();
        await store.SaveAsync();   // .bak exists
        File.WriteAllText(ManifestPath, "{ nope ]");

        var reloaded = new JsonManifestStore(_dir);
        await reloaded.LoadAsync();
        Assert.NotNull(reloaded.RecoveryNote, "the host has a sentence to show");
        Assert.Contains(ManifestPath + ".corrupt", reloaded.RecoveryNote!, "names the quarantined copy");
        Assert.Contains("backup copy", reloaded.RecoveryNote!, "says it started from the .bak");
    }

    // Both files unreadable: the primary must still be quarantined BEFORE the first save replaces it, or the
    // only bytes the user has left are gone; and the host gets told.
    [Test]
    async Task Load_WhenPrimaryAndBakCorrupt_QuarantinesPrimary_AndSetsRecoveryNote()
    {
        File.WriteAllText(ManifestPath, "{ broken primary ]");
        File.WriteAllText(ManifestPath + ".bak", "{ broken bak ]");

        var store = new JsonManifestStore(_dir);
        await store.LoadAsync();

        Assert.Empty(store.Current.Items, "started empty in memory");
        Assert.True(File.Exists(ManifestPath + ".corrupt"), "the unreadable primary was quarantined");
        Assert.Contains("broken primary", File.ReadAllText(ManifestPath + ".corrupt"), "the copy holds the original bytes");
        Assert.NotNull(store.RecoveryNote, "the host has a sentence to show");
        Assert.Contains("empty library", store.RecoveryNote!, "says it started empty");

        await store.SaveAsync();
        Assert.Contains("broken bak", File.ReadAllText(ManifestPath + ".bak"), "the first save leaves the .bak alone");
        Assert.True(File.Exists(ManifestPath + ".corrupt"), "and the quarantined copy survives the save");
    }

    [Test]
    async Task Load_CleanFile_HasNoRecoveryNote()
    {
        var store = new JsonManifestStore(_dir);
        store.Current.Items.Add(new LibraryItem { GogId = 1, Title = "First" });
        await store.SaveAsync();
        var reloaded = new JsonManifestStore(_dir);
        await reloaded.LoadAsync();
        Assert.Null(reloaded.RecoveryNote, "nothing to report on a clean load");
    }

    // A sharing violation is not corruption: a file another process holds open for a moment must be retried,
    // not quarantined and replaced by the .bak. The open is simulated so the case runs on every OS.
    [Test]
    async Task Load_WhenPrimaryBrieflyLocked_RetriesInsteadOfRecovering()
    {
        var store = new JsonManifestStore(_dir);
        store.Current.Items.Add(new LibraryItem { GogId = 1, Title = "Live" });
        await store.SaveAsync();

        var wasOpen = JsonManifestStore.OpenForRead;
        var wasGap = JsonManifestStore.LockRetryGap;
        int opens = 0;
        JsonManifestStore.OpenForRead = p => ++opens <= 2 ? throw new IOException("sharing violation") : File.OpenRead(p);
        JsonManifestStore.LockRetryGap = TimeSpan.FromMilliseconds(5);
        try
        {
            var reloaded = new JsonManifestStore(_dir);
            await reloaded.LoadAsync();
            Assert.Equal(3, opens, "two locked opens, then the third succeeds");
            Assert.Equal("Live", Assert.Single(reloaded.Current.Items).Title, "the primary loaded on the retry");
            Assert.Null(reloaded.RecoveryNote, "no recovery happened");
            Assert.False(File.Exists(ManifestPath + ".corrupt"), "a locked file is never quarantined");
        }
        finally { JsonManifestStore.OpenForRead = wasOpen; JsonManifestStore.LockRetryGap = wasGap; }
    }

    [Test]
    async Task Load_WhenPrimaryStaysLocked_ThrowsLockedInsteadOfRecovering()
    {
        var store = new JsonManifestStore(_dir);
        store.Current.Items.Add(new LibraryItem { GogId = 1, Title = "First" });
        await store.SaveAsync();
        await store.SaveAsync();   // .bak = First
        var bakBefore = File.ReadAllText(ManifestPath + ".bak");

        var wasOpen = JsonManifestStore.OpenForRead;
        var wasGap = JsonManifestStore.LockRetryGap;
        int opens = 0;
        JsonManifestStore.OpenForRead = p => p == ManifestPath && ++opens > 0 ? throw new IOException("sharing violation") : File.OpenRead(p);
        JsonManifestStore.LockRetryGap = TimeSpan.FromMilliseconds(5);
        try
        {
            var reloaded = new JsonManifestStore(_dir);
            ManifestLockedException? locked = null;
            try { await reloaded.LoadAsync(); } catch (ManifestLockedException ex) { locked = ex; }
            Assert.NotNull(locked, "a file locked for good surfaces as locked, not corrupt");
            Assert.Equal(ManifestPath, locked!.Path);
            Assert.Equal(3, opens, "three attempts on the primary before giving up");
            Assert.False(File.Exists(ManifestPath + ".corrupt"), "a locked file is never quarantined");
            Assert.Null(reloaded.RecoveryNote, "no recovery happened");
            Assert.Equal(bakBefore, File.ReadAllText(ManifestPath + ".bak"), "the .bak was left alone");
        }
        finally { JsonManifestStore.OpenForRead = wasOpen; JsonManifestStore.LockRetryGap = wasGap; }
    }

    // After booting from a recovered .bak, the FIRST save must not copy the (corrupt) primary over the
    // good .bak -- one bad boot would otherwise destroy the only surviving copy.
    [Test]
    async Task Save_AfterBakRecovery_DoesNotClobberGoodBak()
    {
        var seed = new JsonManifestStore(_dir);
        seed.Current.Items.Add(new LibraryItem { GogId = 1, Title = "First" });
        await seed.SaveAsync();
        seed.Current.Items[0].Title = "Second";
        await seed.SaveAsync();   // .bak now holds "First" (the good copy)

        File.WriteAllText(ManifestPath, "corrupt");   // primary broken

        var store = new JsonManifestStore(_dir);
        await store.LoadAsync();                       // recovers "First" from .bak, primary untrusted
        store.Current.Items[0].Title = "Recovered";
        await store.SaveAsync();                       // must NOT overwrite .bak with the corrupt primary

        Assert.Contains("First", File.ReadAllText(ManifestPath + ".bak"), ".bak still holds the good copy");
        Assert.Contains("Recovered", File.ReadAllText(ManifestPath), "primary is the fresh clean write");

        var check = new JsonManifestStore(_dir);       // and it re-loads cleanly now
        await check.LoadAsync();
        Assert.Equal("Recovered", Assert.Single(check.Current.Items, "one game").Title, "clean reload");
    }

    // A newer build's unknown fields survive a round-trip through this build instead of being dropped.
    [Test]
    async Task Load_PreservesUnknownFutureFields()
    {
        var store = new JsonManifestStore(_dir);
        store.Current.Items.Add(new LibraryItem { GogId = 7, Title = "T" });
        await store.SaveAsync();

        // Inject a field this build's model doesn't define, at the item level.
        var json = System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(ManifestPath), "\"GogId\":\\s*7,", "\"GogId\":7,\"FutureField\":\"keep-me\",");
        File.WriteAllText(ManifestPath, json);

        var reloaded = new JsonManifestStore(_dir);
        await reloaded.LoadAsync();
        await reloaded.SaveAsync();   // re-save from a model that never knew FutureField

        Assert.Contains("keep-me", File.ReadAllText(ManifestPath), "unknown future field round-trips, not dropped");
    }

    [Test]
    async Task Save_WritesHumanReadableEnums()
    {
        var store = new JsonManifestStore(_dir);
        store.Current.Items.Add(new LibraryItem { GogId = 2, Title = "T", Status = BackupStatus.Complete });
        await store.SaveAsync();

        var json = File.ReadAllText(ManifestPath);
        Assert.Contains("\"Complete\"", json, "enum-as-string so users can read their manifest");
    }
}
