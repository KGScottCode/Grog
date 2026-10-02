// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System.Linq;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Tests.Framework;
using Grog.Core.Volumes;

// The Old Versions/ archive is irreplaceable (GOG no longer ships those builds), so every relocation path
// must CARRY it, never strand it. These lock that the planners include OldVersionFiles.
public class ReorgPlannerOldVersionsTests
{
    static LibraryManifest ManifestOnRoot(string rootId)
    {
        var m = new LibraryManifest { PrimaryRootId = rootId };
        var it = new LibraryItem { GogId = 1, Title = "Game", Slug = "game" };
        it.Files.Add(new GameFile { GameGogId = 1, FileKey = "cur", Kind = FileKind.Installer,
            RootId = rootId, LocalRelativePath = "game/setup.exe", LocalSizeBytes = 100, State = FileState.Verified });
        it.OldVersionFiles.Add(new GameFile { GameGogId = 1, FileKey = "cur#old:setup_v1.exe", Kind = FileKind.Installer,
            RootId = rootId, LocalRelativePath = "Old Versions/game/setup_v1.exe", LocalSizeBytes = 90,
            State = FileState.Verified, IsOldVersion = true });
        m.Items.Add(it);
        return m;
    }

    // A whole-folder repoint (change-folder to a new drive) must include the archive.
    [Test]
    void ForRootPathChange_CarriesOldVersions()
    {
        var m = ManifestOnRoot("r1");
        var job = ReorgPlanner.ForRootPathChange(m, "r1", @"/old", @"/new");

        Assert.Equal(2, job.Moves.Count, "both the current build AND the Old Versions/ archive are planned");
        var old = job.Moves.FirstOrDefault(mv => mv.FileKey.Contains("#old:"));
        Assert.NotNull(old, "the archived build is in the plan, not stranded");
        Assert.Equal("Old Versions/game/setup_v1.exe", old!.ToRel, "archive keeps its relative path (only the root moves)");
    }

    // A per-device move must relocate the archive too, preserving the Old Versions/ shape (NOT reshaping it
    // into a game folder via the layout).
    [Test]
    void ForFileMoves_CarriesOldVersions_PreservingArchiveShape()
    {
        var m = ManifestOnRoot("r1");
        // Two online roots so the layout can resolve source + target paths.
        var baseDir = System.IO.Directory.CreateTempSubdirectory("grog-fm-").FullName;
        var r1 = System.IO.Path.Combine(baseDir, "r1"); var r2 = System.IO.Path.Combine(baseDir, "r2");
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(r1, "Old Versions", "game"));
        System.IO.Directory.CreateDirectory(r2);
        System.IO.File.WriteAllText(System.IO.Path.Combine(r1, "Old Versions", "game", "setup_v1.exe"), "x");
        m.Roots.Add(new BackupRoot { Id = "r1", Label = "One", PathHint = r1 });
        m.Roots.Add(new BackupRoot { Id = "r2", Label = "Two", PathHint = r2 });
        var layout = new BackupLayout(m, r1);

        try
        {
            var oldFile = m.Items[0].OldVersionFiles[0];
            var job = ReorgPlanner.ForFileMoves(m, layout, new[] { (m.Items[0], oldFile) }, "r2");

            var mv = Assert.Single(job.Moves, "the archived build is planned for the device move");
            Assert.Equal("Old Versions/game/setup_v1.exe", mv.ToRel, "archive shape preserved, not reshaped into a game folder");
            Assert.Equal("r2", mv.ToRootId, "repointed to the target device");
        }
        finally { System.IO.Directory.Delete(baseDir, true); }
    }
}
