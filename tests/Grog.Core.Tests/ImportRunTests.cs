// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Runs;
using Grog.Core.Storage;
using Grog.Core.Tests.Framework;
using Grog.Core.Verify;
using Grog.Core.Volumes;

/// <summary>The one import sequence (S2.1, 09-08): register, defer on an empty library, run the deferred
/// imports after a scan, adopt on a populated one.</summary>
[NewBatch]
[Trait("import")]
public class ImportRunTests
{
    private sealed class RecordingHost : NullBackupHost
    {
        public List<string> Logged { get; } = new();
        public override void Log(string message, bool isError = false) => Logged.Add(message);
    }

    private sealed class Rig : IDisposable
    {
        public readonly string Dir;
        public readonly JsonManifestStore Store;
        public readonly RecordingHost Host = new();
        public Rig()
        {
            Dir = Directory.CreateTempSubdirectory("grog-importrun-").FullName;
            Environment.SetEnvironmentVariable("GROG_CONFIG_DIR", Path.Combine(Dir, "_cfg"));
            Store = new JsonManifestStore(GrogPaths.Resolve(Dir));
            Store.LoadAsync().GetAwaiter().GetResult();
            // The real first run SCAFFOLDS the primary folder, so create it here too: an unreachable root
            // correctly keeps its pending flag, and a Rig whose primary never exists on disk would model a
            // state the app cannot produce.
            Directory.CreateDirectory(Path.Combine(Dir, "primary"));
            BackupLayout.EnsurePrimary(Store.Current, Path.Combine(Dir, "primary"));
        }

        /// <summary>A library item whose one installer is 100 bytes.</summary>
        public LibraryItem Item(long id, string title, string slug)
        {
            var it = new LibraryItem { GogId = id, Title = title, Slug = slug };
            it.Files.Add(new GameFile { GameGogId = id, FileKey = $"/downlink/{slug}/en1installer0", Name = "Setup", Kind = FileKind.Installer, ExpectedSizeBytes = 100 });
            Store.Current.Items.Add(it);
            return it;
        }

        /// <summary>An old backup folder holding a game folder with a GOG-named installer of 100 bytes.</summary>
        public string OldBackup(string name, string slug)
        {
            var dir = Path.Combine(Dir, name);
            var game = Path.Combine(dir, slug);
            Directory.CreateDirectory(game);
            File.WriteAllBytes(Path.Combine(game, $"setup_{slug}_1.0.0.1.exe"), new byte[100]);
            return dir;
        }

        public JsonManifestStore Reload()
        {
            var again = new JsonManifestStore(GrogPaths.Resolve(Dir));
            again.LoadAsync().GetAwaiter().GetResult();
            return again;
        }

        public void Dispose() { try { Directory.Delete(Dir, true); } catch { } }
    }

    [Test]
    async Task EmptyLibrary_RegistersTheRoot_FlagsIt_AndDefers()
    {
        using var rig = new Rig();
        var path = rig.OldBackup("old", "syberia");

        var r = await ImportRun.AddAndImportAsync(rig.Store, path, ImportMode.Adopt, rig.Host, CancellationToken.None);

        Assert.Equal(ImportRunStatus.Deferred, r.Status, "no items to match against: deferred");
        Assert.Null(r.Plan, "nothing was planned");
        Assert.Equal(2, rig.Store.Current.Roots.Count, "primary + the new root, registered at once");
        Assert.True(r.Root.PendingImport, "the root owes an import");
        Assert.True(rig.Reload().Current.Roots.Single(x => x.Id == r.Root.Id).PendingImport, "and the promise was saved");
        Assert.True(rig.Host.Logged.Any(l => l.Contains("after your first library scan")), "the host was told why");
    }

    [Test]
    async Task PendingImports_RunOnceItemsExist_ClearTheFlag_AndAdopt()
    {
        using var rig = new Rig();
        var path = rig.OldBackup("old", "syberia");
        var deferred = await ImportRun.AddAndImportAsync(rig.Store, path, ImportMode.Adopt, rig.Host, CancellationToken.None);
        Assert.Equal(ImportRunStatus.Deferred, deferred.Status, "deferred first");

        // The first scan lands.
        var item = rig.Item(10, "Syberia", "syberia");

        var results = await ImportRun.RunPendingAsync(rig.Store, rig.Host, CancellationToken.None);

        // (First-run adopt 09-10) The DEFAULT primary is pending too now, so the run considers both roots.
        // This test is about the NOMINATED one; pick it by id rather than assuming it is alone.
        var r = results.Single(x => x.Root.Id == deferred.Root.Id);
        Assert.Equal(ImportRunStatus.Adopted, r.Status, "adopted");
        Assert.Equal(1, r.Adopted, "the installer was adopted");
        Assert.False(r.Root.PendingImport, "flag cleared");
        Assert.Equal(FileState.Present, item.Files[0].State, "the entry points at the found file");
        Assert.Equal(r.Root.Id, item.Files[0].RootId, "stamped with the import root");
        var again = rig.Reload();
        Assert.False(again.Current.Roots.Single(x => x.Id == r.Root.Id).PendingImport, "cleared flag saved");
        Assert.Equal(FileState.Present, again.Current.ItemById(10)!.Files[0].State, "adoption saved");
        Assert.Equal(0, PendingImports.Runnable(rig.Store.Current).Count, "nothing left to run: the primary was walked in the same pass");
    }

    [Test]
    async Task PopulatedLibrary_AdoptsAtOnce_AndReportsProblems()
    {
        using var rig = new Rig();
        rig.Item(10, "Syberia", "syberia");
        var path = rig.OldBackup("old", "syberia");
        // A second game folder that matches nothing in the library: a problem for a human, not an error.
        Directory.CreateDirectory(Path.Combine(path, "Some Unknown Game"));
        File.WriteAllBytes(Path.Combine(path, "Some Unknown Game", "setup_unknown_1.0.exe"), new byte[50]);

        var r = await ImportRun.AddAndImportAsync(rig.Store, path, ImportMode.Adopt, rig.Host, CancellationToken.None);

        Assert.Equal(ImportRunStatus.Adopted, r.Status, "adopted immediately");
        Assert.False(r.Root.PendingImport, "nothing deferred");
        Assert.Equal(1, r.Adopted, "the match adopted");
        Assert.NotNull(r.Plan, "plan carried for a results screen");
        Assert.Equal(1, r.Problems.Count, "the unmatched folder needs a Find match");
        Assert.True(r.NeedsHuman, "flagged");
        Assert.Equal(1, r.Plan!.UnmatchedGameFolders.Count, "and is in the plan");
    }

    [Test]
    async Task Preview_PlansOnly_AndChangesNothing()
    {
        using var rig = new Rig();
        var item = rig.Item(10, "Syberia", "syberia");
        var path = rig.OldBackup("old", "syberia");

        var r = await ImportRun.AddAndImportAsync(rig.Store, path, ImportMode.Preview, rig.Host, CancellationToken.None);

        Assert.Equal(ImportRunStatus.Previewed, r.Status, "preview");
        Assert.Equal(1, r.Plan!.MatchedFileCount, "the match was found");
        Assert.Equal(0, r.Adopted, "but not adopted");
        Assert.Equal(FileState.NotBackedUp, item.Files[0].State, "entry untouched");
    }

    [Test]
    async Task OfflineRoot_IsSkipped_AndKeepsItsFlag()
    {
        using var rig = new Rig();
        var path = rig.OldBackup("old", "syberia");
        var deferred = await ImportRun.AddAndImportAsync(rig.Store, path, ImportMode.Adopt, rig.Host, CancellationToken.None);
        rig.Item(10, "Syberia", "syberia");
        // The drive went away before the scan finished (still Online in the manifest: no layout re-probed it).
        Directory.Delete(path, true);

        var results = await ImportRun.RunPendingAsync(rig.Store, rig.Host, CancellationToken.None);

        var r = results.Single(x => x.Root.Id == deferred.Root.Id);
        Assert.Equal(ImportRunStatus.Unreachable, r.Status, "not reachable");
        Assert.True(deferred.Root.PendingImport, "flag kept for the next scan");
        Assert.Equal(1, PendingImports.Runnable(rig.Store.Current).Count, "still due: only the unreachable one");
        Assert.True(rig.Host.Logged.Any(l => l.Contains("not reachable")), "said so");
    }

    /// <summary>(First-run adopt 09-10) THE DEFAULT PRIMARY OWES AN IMPORT. It is the one root the user is
    /// never asked about, so it used to be the one root never walked: a library already sitting in the
    /// default path before Grog's first run read as entirely Not Backed Up and would be re-downloaded.</summary>
    [Test]
    void DefaultPrimary_IsMarkedPending_SoTheFirstScanWalksIt()
    {
        using var rig = new Rig();   // the Rig's ctor is the first-run path: EnsurePrimary and nothing else
        var primary = rig.Store.Current.Roots.Single(r => r.Id == rig.Store.Current.PrimaryRootId);
        Assert.True(primary.PendingImport, "the default primary owes an import");
        // Deferred until the library has items to match against; that part is unchanged.
        Assert.True(PendingImports.MustDefer(rig.Store.Current), "nothing to match yet");
        Assert.Equal(0, PendingImports.Runnable(rig.Store.Current).Count, "so it is not runnable yet");
        rig.Item(10, "Syberia", "syberia");
        Assert.Equal(1, PendingImports.Runnable(rig.Store.Current).Count, "runnable once the scan lands");
    }

    // (09-19, owner) Adoption is a name + size match; the bytes are verified straight after, like a download.
    [Test]
    async Task AdoptedFiles_AreVerifiedAtOnce_GoodBytesVerified_BadBytesCorrupt()
    {
        using var rig = new Rig();
        var good = rig.Item(10, "Syberia", "syberia");
        var bad = rig.Item(11, "Gothic", "gothic");
        var path = rig.OldBackup("old", "syberia");
        rig.OldBackup("old", "gothic");
        var md5 = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(new byte[100])).ToLowerInvariant();
        good.Files[0].ExpectedMd5 = md5;
        bad.Files[0].ExpectedMd5 = "00000000000000000000000000000000";   // same size, wrong bytes

        await ImportRun.AddAndImportAsync(rig.Store, path, ImportMode.Adopt, rig.Host, CancellationToken.None);

        Assert.Equal(FileState.Verified, good.Files[0].State, "matching checksum: Verified without a second click");
        Assert.Equal(FileState.Corrupt, bad.Files[0].State, "right size, wrong bytes: caught at adoption");
        Assert.True(rig.Host.Logged.Any(l => l.Contains("Verified adopted files") && l.Contains("1 verified") && l.Contains("1 corrupt")), "and the log says so");
    }

    /// <summary>(09-15 review) Re-adding a DETACHED drive's folder with "Scan and Add" reattaches it and must not
    /// re-adopt the records already on it: Verified stays Verified, Outdated keeps Sync's signal.</summary>
    [Test]
    async Task ReAddingADetachedRoot_ReattachesIt_AndDoesNotRestampItsRecords()
    {
        using var rig = new Rig();
        var item = rig.Item(10, "Syberia", "syberia");
        var path = rig.OldBackup("old", "syberia");
        var first = await ImportRun.AddAndImportAsync(rig.Store, path, ImportMode.Adopt, rig.Host, CancellationToken.None);
        Assert.Equal(ImportRunStatus.Adopted, first.Status, "adopted on the first add");
        var f = item.Files[0];
        Assert.Equal(first.Root.Id, f.RootId);
        f.State = FileState.Verified; f.LastVerifiedAt = DateTimeOffset.UtcNow;   // Verify ran since

        var vol = new VolumeService(rig.Store);
        vol.DetachRoot(first.Root.Id);
        Assert.Equal(RootState.Detached, first.Root.State);

        var again = await ImportRun.AddAndImportAsync(rig.Store, path, ImportMode.Adopt, rig.Host, CancellationToken.None);
        Assert.Equal(first.Root.Id, again.Root.Id, "same root, no duplicate");
        Assert.True(first.Root.State != RootState.Detached, "reattached");
        Assert.Equal(FileState.Verified, f.State, "a record already holding bytes here is not re-adopted");
        Assert.Equal(1, rig.Store.Current.Items.Sum(i => i.Files.Count), "no duplicate records");
    }
    /// <summary>(1284) A storage back under another path (a re-lettered stick): "Scan and Add" on the new folder
    /// recognises it by its files and re-points the SAME root there; no second root, nothing re-adopted, the old
    /// path forgotten. The folder itself carries nothing of Grog's.</summary>
    [Test]
    async Task ScanAndAdd_OnAStorageAtANewPath_RepointsTheExistingRoot()
    {
        using var rig = new Rig();
        var item = rig.Item(10, "Syberia", "syberia");
        var path = rig.OldBackup("old", "syberia");
        var first = await ImportRun.AddAndImportAsync(rig.Store, path, ImportMode.Adopt, rig.Host, CancellationToken.None);
        Assert.Equal(ImportRunStatus.Adopted, first.Status);
        var f = item.Files[0];
        f.State = FileState.Verified; f.LastVerifiedAt = DateTimeOffset.UtcNow;

        // The drive comes back under another letter: the whole folder is elsewhere, the old path is gone.
        var moved = Path.Combine(rig.Dir, "relettered");
        Directory.Move(path, moved);
        Assert.False(Directory.Exists(path));
        Assert.Equal(first.Root.Id, ImportRun.MatchExistingRoot(rig.Store, moved)!.Id, "the folder's files name the root");

        var again = await ImportRun.AddAndImportAsync(rig.Store, moved, ImportMode.Adopt, rig.Host, CancellationToken.None);
        Assert.True(again.MatchedExisting, "matched, not added");
        Assert.Equal(first.Root.Id, again.Root.Id, "the same root");
        Assert.Equal(Path.GetFullPath(moved), first.Root.PathHint, "re-pointed to the new folder");
        Assert.Equal(1, rig.Store.Current.Roots.Count(r => r.Id != rig.Store.Current.PrimaryRootId), "no second root");
        Assert.Equal(FileState.Verified, f.State, "its records are not re-adopted");

        // A folder with the same shape whose files are NOT this root's (another stick) is a new storage.
        var other = rig.OldBackup("otherstick", "syberia");
        File.WriteAllBytes(Path.Combine(other, "syberia", "setup_syberia_1.0.0.1.exe"), new byte[100]);
        Assert.Null(ImportRun.MatchExistingRoot(rig.Store, other), "the root is live at its own path: another folder with the same files is not it");
    }

    /// <summary>The CLI's --verify-checksums: an adopted file with no checksum on record asks GOG for one and
    /// hashes against it, so it lands Verified rather than size-only. Off by default, and no fetcher means no fetch.</summary>
    [Test]
    async Task VerifyChecksumsOption_FetchesTheMissingChecksum_AndVerifiesAgainstIt()
    {
        using var rig = new Rig();
        var item = rig.Item(10, "Syberia", "syberia");
        var path = rig.OldBackup("old", "syberia");
        var md5 = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(new byte[100])).ToLowerInvariant();
        int fetches = 0;
        var options = new ImportRun.Options
        {
            FetchMissingChecksums = true,
            FetchCurrentServerMd5 = (_, _) => { fetches++; return Task.FromResult<string?>(md5); },
        };

        await ImportRun.AddAndImportAsync(rig.Store, path, ImportMode.Adopt, rig.Host, CancellationToken.None, options);

        Assert.Equal(1, fetches, "one fetch for the one file without a checksum");
        Assert.Equal(FileState.Verified, item.Files[0].State, "hashed against GOG's checksum");
        Assert.Equal(md5, item.Files[0].ExpectedMd5, "and the checksum is now on record");
    }

    [Test]
    async Task VerifyChecksumsOption_WithoutAFetcher_StaysSizeOnly()
    {
        using var rig = new Rig();
        var item = rig.Item(10, "Syberia", "syberia");
        var path = rig.OldBackup("old", "syberia");
        var options = new ImportRun.Options { FetchMissingChecksums = true };

        await ImportRun.AddAndImportAsync(rig.Store, path, ImportMode.Adopt, rig.Host, CancellationToken.None, options);

        Assert.True(item.Files[0].State != FileState.Verified, "nothing to hash against: not Verified");
        Assert.True(string.IsNullOrEmpty(item.Files[0].ExpectedMd5), "no checksum invented");
    }
}
