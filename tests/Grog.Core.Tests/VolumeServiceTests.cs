// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System;
using System.IO;
using System.Linq;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Storage;
using Grog.Core.Tests.Framework;
using Grog.Core.Volumes;

public class VolumeServiceTests
{
    static (JsonManifestStore store, string root, BackupLayout layout) NewSetup()
    {
        var baseDir = Directory.CreateTempSubdirectory("grog-vs-").FullName;
        var root = Path.Combine(baseDir, "primary");
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("GROG_CONFIG_DIR", Path.Combine(baseDir, "_cfg"));
        var store = new JsonManifestStore(GrogPaths.Resolve(root));
        store.LoadAsync().GetAwaiter().GetResult();
        var layout = new BackupLayout(store.Current, root);
        return (store, root, layout);
    }

    /// <summary>A second location must live beside the primary, never inside it.</summary>
    static string Sibling(string root, string name) => Path.GetFullPath(Path.Combine(root, "..", name));

    static void Cleanup(string root) => Directory.Delete(Path.GetFullPath(Path.Combine(root, "..")), true);

    [Test]
    void AddRoot_IsIdempotent_SamePathReturnsSameRoot()
    {
        var (store, root, _) = NewSetup();
        try
        {
            var vol = new VolumeService(store);
            var p = Sibling(root, "Volume_02");
            var first = vol.AddRoot(p, "Archive");
            int countAfterFirst = vol.Roots.Count;
            var second = vol.AddRoot(p);              // same folder again
            Assert.Equal(first.Id, second.Id, "same path -> same root id");
            Assert.Equal(countAfterFirst, vol.Roots.Count, "no duplicate root added");
        }
        finally { Cleanup(root); }
    }

    [Test]
    async System.Threading.Tasks.Task AddRoot_AddsAndBindsByPath()
    {
        var (store, root, _) = NewSetup();
        try
        {
            var vol = new VolumeService(store);
            var vol2Path = Sibling(root, "Volume_02");
            var r = vol.AddRoot(vol2Path, "Archive");
            await store.SaveAsync();

            Assert.Equal("Archive", r.Label, "label set");

            // A fresh layout should find & bind it as online.
            var layout2 = new BackupLayout(store.Current, root);
            Assert.True(layout2.IsOnline(r.Id), "second root online after rescan");
        }
        finally { Cleanup(root); }
    }

    [Test]
    async System.Threading.Tasks.Task Policy_RoutesExtrasToSecondRoot()
    {
        var (store, root, _) = NewSetup();
        try
        {
            var vol = new VolumeService(store);
            var archive = vol.AddRoot(Sibling(root, "Volume_02"), "Archive");
            vol.SetRolePolicy(ContentRole.Extras, "Archive");
            await store.SaveAsync();

            var primary = store.Current.PrimaryRootId!;
            Assert.Equal(primary, store.Current.Routing.ResolveRootId(1, FileKind.Installer, primary, ExtrasPlacement.SeparateByGame), "games→primary");
            Assert.Equal(archive.Id, store.Current.Routing.ResolveRootId(1, FileKind.Extra, primary, ExtrasPlacement.SeparateByGame), "extras→archive");
        }
        finally { Cleanup(root); }
    }

    [Test]
    async System.Threading.Tasks.Task Relocate_MovesFilesToTargetRoot()
    {
        var (store, root, _) = NewSetup();
        try
        {
            var vol = new VolumeService(store);
            var archive = vol.AddRoot(Sibling(root, "Volume_02"), "Archive");
            var layout = new BackupLayout(store.Current, root);

            // A game with one file present on the primary root.
            var game = new LibraryItem { GogId = 3, Title = "Mover", Slug = "mover" };
            var srcDir = Path.Combine(root, "mover");
            Directory.CreateDirectory(srcDir);
            var srcFile = Path.Combine(srcDir, "setup.exe");
            await File.WriteAllTextAsync(srcFile, "payload");
            game.Files.Add(new GameFile
            {
                GameGogId = 3, Kind = FileKind.Installer, Name = "setup",
                RootId = store.Current.PrimaryRootId,
                LocalRelativePath = Path.Combine("mover", "setup.exe"),
                State = FileState.Present,
                ExpectedSizeBytes = 7,
            });
            store.Current.Items.Add(game);
            await store.SaveAsync();

            var r = await vol.RelocateGameAsync(game, "Archive", layout);
            Assert.Equal(1, r.Moved, "one file moved");
            Assert.False(File.Exists(srcFile), "source gone");
            // A move asks the layout where the file belongs, so it lands in the CURRENT shape
            // (Games/<slug>), not the old flat "<slug>" one. Moving used to silently rebuild v3 paths.
            Assert.True(File.Exists(Path.Combine(Sibling(root, "Volume_02"), "Games", "mover", "setup.exe")), "file at target, in the current layout");
            Assert.Equal(archive.Id, game.Files[0].RootId, "root id updated");
        }
        finally { Cleanup(root); }
    }

    [Test]
    async System.Threading.Tasks.Task Replace_RepointsFilesAndMarksMissing()
    {
        var (store, root, _) = NewSetup();
        try
        {
            var vol = new VolumeService(store);
            var archive = vol.AddRoot(Sibling(root, "Volume_02"), "Archive");
            var oldId = archive.Id;

            // A file living on the archive root, marked downloaded.
            var game = new LibraryItem { GogId = 4, Title = "OnArchive" };
            game.Files.Add(new GameFile
            {
                GameGogId = 4, Kind = FileKind.Installer, RootId = oldId,
                LocalRelativePath = "onarchive/x.bin", State = FileState.Verified,
            });
            store.Current.Items.Add(game);
            await store.SaveAsync();

            var newPath = Sibling(root, "Volume_03");
            vol.ReplaceRoot("Archive", newPath);
            await store.SaveAsync();

            Assert.True(archive.Id != oldId, "root got a new id");
            Assert.Contains(oldId, string.Join(",", archive.PriorIds), "old id in history");
            Assert.Equal(archive.Id, game.Files[0].RootId, "file re-pointed to new id");
            Assert.Equal(FileState.Missing, game.Files[0].State, "marked missing for re-fetch");
        }
        finally { Cleanup(root); }
    }

    // Replacing a dead primary must repoint the Old Versions/ archive AND legacy null-RootId files (which
    // resolve to the primary), or they stay marked backed-up while pointing at the new empty drive.
    [Test]
    async System.Threading.Tasks.Task Replace_RepointsOldVersionsAndNullRootedPrimaryFiles()
    {
        var (store, root, _) = NewSetup();
        try
        {
            var vol = new VolumeService(store);
            var primaryId = store.Current.PrimaryRootId!;   // the primary root the setup created

            var game = new LibraryItem { GogId = 9, Title = "OnPrimary" };
            game.Files.Add(new GameFile { GameGogId = 9, Kind = FileKind.Installer, RootId = null,
                LocalRelativePath = "onprimary/x.bin", State = FileState.Verified });   // legacy null RootId => primary
            game.OldVersionFiles.Add(new GameFile { GameGogId = 9, FileKey = "x#old:x_v1.bin", Kind = FileKind.Installer,
                RootId = primaryId, LocalRelativePath = "Old Versions/onprimary/x_v1.bin",
                State = FileState.Verified, IsOldVersion = true });
            store.Current.Items.Add(game);
            await store.SaveAsync();

            var newPath = Sibling(root, "Primary_New");
            vol.ReplaceRoot(primaryId, newPath);
            await store.SaveAsync();

            var it = store.Current.Items.First(i => i.GogId == 9);
            Assert.Equal(store.Current.PrimaryRootId, it.Files[0].RootId, "null-rooted primary file repointed to the new id");
            Assert.Equal(FileState.Missing, it.Files[0].State, "and marked missing for re-fetch");
            Assert.Equal(store.Current.PrimaryRootId, it.OldVersionFiles[0].RootId, "the archive is repointed too, not stranded");
        }
        finally { Cleanup(root); }
    }

    [Test]
    async System.Threading.Tasks.Task RemoveRoot_RefusesWhenHoldingFiles_UnlessForced()
    {
        var (store, root, _) = NewSetup();
        try
        {
            var vol = new VolumeService(store);
            var archive = vol.AddRoot(Sibling(root, "Volume_02"), "Archive");
            var game = new LibraryItem { GogId = 5, Title = "Holder" };
            game.Files.Add(new GameFile { GameGogId = 5, RootId = archive.Id, LocalRelativePath = "h/x" });
            store.Current.Items.Add(game);

            bool threw = false;
            try { vol.RemoveRoot("Archive"); } catch { threw = true; }
            Assert.True(threw, "refuses while holding files");

            vol.RemoveRoot("Archive", force: true);
            Assert.True(!store.Current.Roots.Any(r => r.Id == archive.Id), "removed with --force");
        }
        finally { Cleanup(root); }
    }

    /// <summary>(09-13) The shelf. A detached drive keeps every record and count, is never bound (even with its
    /// folder present), is no placement target, does not count as an offline drive, and comes back with Reattach.</summary>
    [Test]
    void DetachRoot_KeepsFilesCounted_NeverBinds_AndReattaches()
    {
        var (store, root, _) = NewSetup();
        try
        {
            var vol = new VolumeService(store);
            var shelf = vol.AddRoot(Sibling(root, "Volume_02"), "Shelf");
            store.Current.Routing.SetRole(ContentRole.Extras, shelf.Id);
            var game = new LibraryItem { GogId = 9, Title = "OnShelf" };
            var f = new GameFile { GameGogId = 9, RootId = shelf.Id, LocalRelativePath = "g/x", State = FileState.Verified, ExpectedSizeBytes = 5 };
            game.Files.Add(f);
            store.Current.Items.Add(game);

            bool threw = false;
            try { vol.DetachRoot(store.Current.PrimaryRootId!); } catch { threw = true; }
            Assert.True(threw, "the primary cannot be shelved");

            vol.DetachRoot("Shelf");
            Assert.Equal(RootState.Detached, shelf.State);
            Assert.Equal(shelf.Id, f.RootId, "record kept"); Assert.Equal(FileState.Verified, f.State, "still backed up");
            Assert.True(Grog.Core.Sync.BackupScope.HasLocalCopy(f), "counts as backed up");
            Assert.False(Grog.Core.Sync.BackupScope.NeedsFetch(f), "never re-queued");
            Assert.Equal(store.Current.PrimaryRootId, store.Current.Routing.RoleRoots[ContentRole.Extras], "routing falls back to the primary");

            var layout = new BackupLayout(store.Current, root);   // its folder IS on disk
            Assert.False(layout.IsOnline(shelf.Id), "never bound while detached");
            Assert.Equal(RootState.Detached, shelf.State, "the scan leaves it Detached");
            Assert.True(vol.DeviceSpaces(layout).All(d => d.RootId != shelf.Id), "not a placement target");
            Assert.Equal(1, vol.ActiveRoots.Count(), "frees its slot"); Assert.Equal(1, vol.DetachedRoots.Count());

            vol.ReattachRoot("Shelf");
            var layout2 = new BackupLayout(store.Current, root);
            Assert.True(layout2.IsOnline(shelf.Id), "reattached and bound");
            Assert.Equal(RootState.Online, shelf.State);
        }
        finally { Cleanup(root); }
    }

    [Test]
    async System.Threading.Tasks.Task RemoveRoot_ForgetsLocationsAndClearsBackedUpState()
    {
        var (store, root, _) = NewSetup();
        try
        {
            var vol = new VolumeService(store);
            var archive = vol.AddRoot(Sibling(root, "Volume_02"), "Archive");
            var game = new LibraryItem { GogId = 7, Title = "OnArchive" };
            game.Files.Add(new GameFile { GameGogId = 7, RootId = archive.Id, LocalRelativePath = "g/x", State = FileState.Verified });
            store.Current.Items.Add(game);

            vol.RemoveRoot("Archive", force: true);

            var f = store.Current.Items.First(i => i.GogId == 7).Files[0];
            Assert.Null(f.RootId, "location forgotten (device gone)");
            Assert.Equal(FileState.NotBackedUp, f.State, "no longer counted as backed up");
            Assert.Null(f.LocalRelativePath, "the path goes too: with RootId null it resolved to the PRIMARY");
            Assert.Equal("x", f.ResolvedFileName, "its name is kept for the import that re-adopts it on a re-add");
        }
        finally { Cleanup(root); }
    }

    [Test]
    async System.Threading.Tasks.Task SetPrimary_MakesRootTheDefault()
    {
        var (store, root, _) = NewSetup();
        try
        {
            var vol = new VolumeService(store);
            var archive = vol.AddRoot(Sibling(root, "Volume_02"), "Archive");
            Assert.True(store.Current.PrimaryRootId != archive.Id, "archive is not primary yet");
            vol.SetPrimary("Archive");
            Assert.Equal(archive.Id, store.Current.PrimaryRootId, "archive is now the default device");
        }
        finally { Cleanup(root); }
    }

    [Test]
    async System.Threading.Tasks.Task AddRoot_Removable_SetsFlag()
    {
        var (store, root, _) = NewSetup();
        try
        {
            var vol = new VolumeService(store);
            var usb = vol.AddRoot(Sibling(root, "USB"), "USB", removable: true);
            Assert.True(usb.Removable, "removable flag set");
        }
        finally { Cleanup(root); }
    }

    [Test]
    async System.Threading.Tasks.Task PinRole_Extras_ThenClear()
    {
        var (store, root, _) = NewSetup();
        try
        {
            var vol = new VolumeService(store);
            var sec = vol.AddRoot(Sibling(root, "Second"), "Second");

            vol.PinRole("Second", Grog.Core.Models.ContentRole.Extras);
            Assert.True(store.Current.Routing.RoleRoots.TryGetValue(Grog.Core.Models.ContentRole.Extras, out var rid) && rid == sec.Id,
                "extras routed to the secondary");

            vol.PinRole("Second", null);
            Assert.True(!store.Current.Routing.RoleRoots.Values.Contains(sec.Id), "null role clears the pin");
        }
        finally { Cleanup(root); }
    }

    [Test]
    void RemoveRoot_RefusesPrimary()
    {
        var (store, root, _) = NewSetup();
        try
        {
            var vol = new VolumeService(store);
            var primaryLabel = store.Current.Roots.First(r => r.Id == store.Current.PrimaryRootId).Label;
            bool threw = false;
            try { vol.RemoveRoot(primaryLabel); } catch { threw = true; }
            Assert.True(threw, "primary can't be removed");
        }
        finally { Cleanup(root); }
    }

    /// <summary>(09-14) Re-adding a detached root's folder (Add / Scan and Add) brings it back instead of
    /// returning it still shelved and invisible.</summary>
    [Test]
    void AddRoot_OnADetachedRootsPath_Reattaches()
    {
        var (store, root, _) = NewSetup();
        try
        {
            var vol = new VolumeService(store);
            var shelf = vol.AddRoot(Sibling(root, "Volume_02"), "Shelf");
            vol.DetachRoot("Shelf");
            Assert.Equal(RootState.Detached, shelf.State);
            var again = vol.AddRoot(Sibling(root, "Volume_02"));
            Assert.True(ReferenceEquals(shelf, again), "same root, no duplicate");
            Assert.Equal(RootState.Offline, shelf.State, "re-adding the folder reattaches it");
            Assert.Equal(2, vol.ActiveRoots.Count());
        }
        finally { Cleanup(root); }
    }

    /// <summary>(09-14) Other storage swaps into a slot in one step; a primary swap stamps the outgoing primary's
    /// implicit (null-RootId) files so they are not re-attributed to the new primary.</summary>
    [Test]
    void SwapIntoSlot_TakesTheSlot_AndAPrimarySwapStampsImplicitFiles()
    {
        var (store, root, _) = NewSetup();
        try
        {
            var vol = new VolumeService(store);
            var primary = store.Current.PrimaryRootId!;
            var sec = vol.AddRoot(Sibling(root, "Volume_02"), "Sec");
            var shelf = vol.AddRoot(Sibling(root, "Volume_03"), "Shelf");
            vol.DetachRoot("Shelf");
            var game = new LibraryItem { GogId = 7, Title = "Implicit" };
            var f = new GameFile { GameGogId = 7, RootId = null, LocalRelativePath = "g/a", State = FileState.Verified, ExpectedSizeBytes = 1 };
            game.Files.Add(f); store.Current.Items.Add(game);

            vol.SwapIntoSlot(shelf.Id, sec.Id);
            Assert.Equal(RootState.Detached, sec.State, "the slot holder goes to the shelf");
            Assert.Equal(RootState.Offline, shelf.State, "the other drive takes the slot");
            Assert.Null(f.RootId, "a secondary swap leaves implicit files alone");

            vol.SwapIntoSlot(sec.Id, primary);
            Assert.Equal(sec.Id, store.Current.PrimaryRootId, "primary role moved");
            Assert.Equal(primary, f.RootId, "the implicit file now names the drive its bytes are on");
            Assert.Equal(RootState.Detached, store.Current.Roots.First(r => r.Id == primary).State, "old primary shelved");
            Assert.Equal(2, vol.ActiveRoots.Count());
        }
        finally { Cleanup(root); }
    }

    // 1281: adding the primary's own folder (or a tracked drive's) as a second storage is refused by name, not nested.
    [Test]
    void FindSamePath_NamesTheTrackedRoot_AndNestingDoesNot()
    {
        var (store, root, _) = NewSetup();
        try
        {
            var vol = new VolumeService(store);
            var primary = store.Current.Roots.Single(r => r.Id == store.Current.PrimaryRootId);
            Assert.Equal(primary.Id, vol.FindSamePath(root)!.Id, "the primary's own path");
            Assert.Equal(primary.Id, vol.FindSamePath(root + Path.DirectorySeparatorChar)!.Id, "trailing separator ignored");
            Assert.Null(vol.FindNestingConflict(root), "the same path is not nesting");
            Assert.Null(vol.FindSamePath(Sibling(root, "Volume_02")), "a sibling is not tracked");
            Assert.NotNull(vol.FindNestingConflict(Path.Combine(root, "inner")), "a child is nesting");
        }
        finally { Cleanup(root); }
    }

    /// <summary>(09-15 review) SetPrimary moves the ROLE pins (EnsurePrimary stamps them with the first primary
    /// explicitly) and stamps only records that hold bytes.</summary>
    [Test]
    void SetPrimary_MovesRolePins_StampsOnlyHeldBytes()
    {
        var (store, root, _) = NewSetup();
        try
        {
            var vol = new VolumeService(store);
            var old = store.Current.PrimaryRootId!;
            var sec = vol.AddRoot(Sibling(root, "Volume_02"), "Sec");
            store.Current.Routing.SetRole(ContentRole.Games, old); store.Current.Routing.SetRole(ContentRole.Extras, old);
            var game = new LibraryItem { GogId = 5, Title = "G" };
            var held = new GameFile { GameGogId = 5, FileKey = "a", RootId = null, LocalRelativePath = "g/a", State = FileState.Verified, ExpectedSizeBytes = 1 };
            var leftover = new GameFile { GameGogId = 5, FileKey = "b", RootId = null, LocalRelativePath = "g/b", State = FileState.NotBackedUp, ExpectedSizeBytes = 1 };   // a Forgotten drive's record
            game.Files.Add(held); game.Files.Add(leftover); store.Current.Items.Add(game);

            vol.SetPrimary(sec.Id);
            Assert.Equal(sec.Id, store.Current.Routing.RoleRoots[ContentRole.Games], "Games role follows the primary");
            Assert.Equal(sec.Id, store.Current.Routing.RoleRoots[ContentRole.Extras], "Extras role follows the primary");
            Assert.Equal(old, held.RootId, "held bytes stamped with the drive they are on");
            Assert.Null(leftover.RootId, "a path with no bytes behind it is not stamped");
        }
        finally { Cleanup(root); }
    }

    [Test]
    void RenameRoot_And_HasCustomLabel()
    {
        var (store, root, _) = NewSetup();
        try
        {
            var vol = new VolumeService(store);
            var r = vol.AddRoot(Sibling(root, "Volume_02"));
            Assert.False(VolumeService.HasCustomLabel(r), "folder name is the default");
            r.Label = "Secondary"; Assert.False(VolumeService.HasCustomLabel(r), "old role words are not names");
            vol.RenameRoot(r.Id, "  Old laptop drive ");
            Assert.Equal("Old laptop drive", r.Label); Assert.True(VolumeService.HasCustomLabel(r));
            vol.RenameRoot(r.Id, "   ");
            Assert.Equal("Volume_02", r.Label, "blank resets to the folder name"); Assert.False(VolumeService.HasCustomLabel(r));
        }
        finally { Cleanup(root); }
    }
}
