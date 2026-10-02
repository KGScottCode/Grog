// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Preservation;
using Grog.Core.Storage;
using Grog.Core.Tests.Framework;
using Grog.Core.Volumes;

public class PreservationTests
{
    static (JsonManifestStore store, string root, BackupLayout layout) NewSetup()
    {
        var root = Directory.CreateTempSubdirectory("grog-pres-").FullName;
        // Own config dir: the primary root's PathHint is now the fact, so a manifest left behind by another
        // test class would bind the layout to ITS folder, not this one.
        Environment.SetEnvironmentVariable("GROG_CONFIG_DIR", Path.Combine(root, "_cfg"));
        var store = new JsonManifestStore(GrogPaths.Resolve(root));
        store.LoadAsync().GetAwaiter().GetResult();
        var layout = new BackupLayout(store.Current, root);
        return (store, root, layout);
    }

    [Test]
    void Health_CountsStatusesAndBytes()
    {
        var (store, root, layout) = NewSetup();
        try
        {
            var g1 = new LibraryItem { GogId = 1, Title = "Done", Status = BackupStatus.Complete };
            g1.Files.Add(new GameFile { State = FileState.Verified, LocalSizeBytes = 1000 });
            var g2 = new LibraryItem { GogId = 2, Title = "Missing", Status = BackupStatus.NotBackedUp };
            var g3 = new LibraryItem { GogId = 3, Title = "Broken", Status = BackupStatus.Corrupt };
            g3.Files.Add(new GameFile { State = FileState.Corrupt, LocalSizeBytes = 500 });
            store.Current.Items.AddRange(new[] { g1, g2, g3 });

            var pres = new PreservationService(store, layout);
            var h = pres.Health();
            Assert.Equal(3, h.TotalItems, "3 items");
            Assert.Equal(1, h.Complete, "1 complete");
            Assert.Equal(1, h.NotBackedUp, "1 not downloaded");
            Assert.Equal(1, h.CorruptItems, "1 error");
            Assert.Equal(1, h.CorruptFiles, "1 corrupt file");
            Assert.Equal(1500, h.BytesOnDisk, "bytes summed");
            Assert.True(h.RootCoverage.Count >= 1, "root coverage present");
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    void Delisted_DetectsFlaggedAndEmptyGames()
    {
        var (store, root, layout) = NewSetup();
        try
        {
            var flagged = new LibraryItem { GogId = 1, Title = "Gone", IsDelisted = true, Type = ProductType.Game };
            flagged.Files.Add(new GameFile { Kind = FileKind.Installer });
            var emptyGame = new LibraryItem { GogId = 2, Title = "NoFiles", Type = ProductType.Game };
            var normalPack = new LibraryItem { GogId = 3, Title = "Pack", Type = ProductType.Pack }; // empty but a pack → not at-risk
            store.Current.Items.AddRange(new[] { flagged, emptyGame, normalPack });

            var pres = new PreservationService(store, layout);
            var d = pres.DelistedItems();
            Assert.Equal(2, d.Count, "flagged + empty game");
            Assert.True(d.Any(x => x.Title == "Gone"), "flagged detected");
            Assert.True(d.Any(x => x.Title == "NoFiles"), "empty game detected");
            Assert.True(!d.Any(x => x.Title == "Pack"), "empty pack not flagged");
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    async System.Threading.Tasks.Task Serials_ExportsWhenPresent()
    {
        var (store, root, layout) = NewSetup();
        try
        {
            store.Current.Items.Add(new LibraryItem { GogId = 1, Title = "KeyGame", SerialKey = "ABC-123" });
            store.Current.Items.Add(new LibraryItem { GogId = 2, Title = "NoKey" });
            var pres = new PreservationService(store, layout);

            var serials = pres.SerialKeys();
            Assert.Equal(1, serials.Count, "one serial");

            var cfg = GrogPaths.Resolve(root).ConfigDir;
            var path = await pres.ExportSerialsAsync(cfg);
            Assert.True(path is not null && File.Exists(path), "exported file exists");
            Assert.True((await File.ReadAllTextAsync(path!)).Contains("ABC-123"), "serial written");
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    async System.Threading.Tasks.Task ZipCheck_FlagsCorruptArchive()
    {
        var (store, root, layout) = NewSetup();
        try
        {
            // Good zip.
            var goodDir = Path.Combine(root, "g", "extras");
            Directory.CreateDirectory(goodDir);
            var goodZip = Path.Combine(goodDir, "good.zip");
            using (var z = ZipFile.Open(goodZip, ZipArchiveMode.Create))
                z.CreateEntry("a.txt");

            // Bad zip (not really a zip).
            var badZip = Path.Combine(goodDir, "bad.zip");
            await File.WriteAllTextAsync(badZip, "this is not a zip file at all");

            var game = new LibraryItem { GogId = 1, Title = "Z", Slug = "g" };
            game.Files.Add(new GameFile { Kind = FileKind.Extra, LocalRelativePath = Path.Combine("g", "extras", "good.zip"), State = FileState.Present });
            game.Files.Add(new GameFile { Kind = FileKind.Extra, LocalRelativePath = Path.Combine("g", "extras", "bad.zip"), State = FileState.Present });
            store.Current.Items.Add(game);
            await store.SaveAsync();

            var pres = new PreservationService(store, layout);
            var r = await pres.CheckZipsAsync();
            Assert.Equal(2, r.Checked, "2 checked");
            Assert.Equal(1, r.Ok, "1 ok");
            Assert.Equal(1, r.Bad, "1 bad");
            Assert.Equal(FileState.Corrupt, game.Files[1].State, "bad flagged corrupt");
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    async System.Threading.Tasks.Task Orphans_DetectsUnknownFiles_DryRunDoesNotMove()
    {
        var (store, root, layout) = NewSetup();
        try
        {
            // A known file recorded in the manifest.
            var knownDir = Path.Combine(root, "game");
            Directory.CreateDirectory(knownDir);
            var known = Path.Combine(knownDir, "setup.exe");
            await File.WriteAllTextAsync(known, "known");
            var game = new LibraryItem { GogId = 1, Title = "G" };
            game.Files.Add(new GameFile { RootId = store.Current.PrimaryRootId, LocalRelativePath = Path.Combine("game", "setup.exe") });
            store.Current.Items.Add(game);

            // An orphan file not in the manifest.
            var orphan = Path.Combine(knownDir, "stray.bin");
            await File.WriteAllTextAsync(orphan, "orphan");

            var pres = new PreservationService(store, layout);
            var dry = pres.HandleOrphans(move: false);
            Assert.True(dry.Orphans.Any(o => o.EndsWith("stray.bin")), "orphan detected");
            Assert.True(!dry.Orphans.Any(o => o.EndsWith("setup.exe")), "known not flagged");
            Assert.Equal(0, dry.Moved, "dry run moves nothing");
            Assert.True(File.Exists(orphan), "orphan still in place after dry run");

            var moved = pres.HandleOrphans(move: true);
            Assert.True(moved.Moved >= 1, "moved on --move");
            Assert.True(!File.Exists(orphan), "orphan relocated");
        }
        finally { Directory.Delete(root, true); }
    }

    // Sweep 2 #27: orphans --move carried the Old Versions archive and live partials away.
    [Test]
    void Orphans_NeverTouchTheArchive_Partials_CloudSaves_OrItsOwnFolder()
    {
        var (store, root, layout) = NewSetup();
        try
        {
            void Put(params string[] rel) { var p = Path.Combine(new[] { root }.Concat(rel).ToArray()); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, "x"); }
            Put("Old Versions", "game", "setup_old.exe");
            Put(".grog-tmp", "abc.part");
            Put("Games", "game", "download.part");
            Put("Cloud Saves", "bob", "game", "cloud-saves", "20260101-000000", "slot.sav");
            Put(PreservationService.OrphanedFolder, "earlier.bin");
            Put("Games", "game", "stray.bin");

            var res = new PreservationService(store, layout).HandleOrphans(move: true);

            Assert.Equal(1, res.Orphans.Count, string.Join(" | ", res.Orphans));
            Assert.True(res.Orphans[0].EndsWith("stray.bin"), "only the real stray");
            Assert.True(File.Exists(Path.Combine(root, "Old Versions", "game", "setup_old.exe")), "the archive stays");
            Assert.True(File.Exists(Path.Combine(root, ".grog-tmp", "abc.part")), "a live partial stays");
            Assert.True(File.Exists(Path.Combine(root, "Cloud Saves", "bob", "game", "cloud-saves", "20260101-000000", "slot.sav")), "cloud saves stay");
        }
        finally { Directory.Delete(root, true); }
    }
}
