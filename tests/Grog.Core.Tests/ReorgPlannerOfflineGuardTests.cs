// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System.Linq;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Tests.Framework;
using Grog.Core.Volumes;

// A move must NEVER silently drop files whose drive is offline/missing -- that looks like a black hole to
// the user (they pick "move" and some files just vanish from the plan). Every planner records the offline
// drive on the job so the caller blocks the WHOLE move. These lock that reporting.
public class ReorgPlannerOfflineGuardTests
{
    // Primary bound to a real temp dir; a second root whose PathHint does not exist -> offline.
    static (LibraryManifest m, BackupLayout layout, string primary) TwoRoots_SecondOffline(string secondFlatRel)
    {
        var primary = System.IO.Directory.CreateTempSubdirectory("grog-off-").FullName;
        var m = new LibraryManifest();
        m.Roots.Add(new BackupRoot { Id = "r1", Label = "Primary", PathHint = primary });
        m.Roots.Add(new BackupRoot { Id = "r2", Label = "Archive", PathHint = System.IO.Path.Combine(primary, "does-not-exist-r2") });
        m.PrimaryRootId = "r1";
        m.SetExtrasLayout(ExtrasPlacement.SeparateByType);

        var it = new LibraryItem { GogId = 1, Title = "Game", Slug = "game" };
        // A file on the OFFLINE root, in the old flat layout so it genuinely needs a move.
        it.Files.Add(new GameFile { GameGogId = 1, FileKey = "f", Kind = FileKind.Installer,
            RootId = "r2", LocalRelativePath = secondFlatRel, LocalSizeBytes = 100, State = FileState.Verified });
        m.Items.Add(it);

        var layout = new BackupLayout(m, primary);   // binds r1; r2 stays offline (its PathHint is absent)
        return (m, layout, primary);
    }

    [Test]
    void ForLayoutChange_OfflineDrive_IsReportedAndItsMovesExcluded()
    {
        var (m, layout, _) = TwoRoots_SecondOffline("setup.exe");   // flat -> needs Games/game/setup.exe

        var job = ReorgPlanner.ForLayoutChange(m, layout);

        Assert.True(job.HasUnavailableDrives, "the offline drive is reported so the caller can block");
        Assert.Contains("Archive", string.Join(",", job.UnavailableDrives));
        Assert.Empty(job.Moves);   // its move is NOT silently included -- the whole job blocks
    }

    [Test]
    void ForLayoutChange_AllOnline_NoBlock()
    {
        var primary = System.IO.Directory.CreateTempSubdirectory("grog-on-").FullName;
        var m = new LibraryManifest { PrimaryRootId = "r1" };
        m.SetExtrasLayout(ExtrasPlacement.SeparateByType);
        m.Roots.Add(new BackupRoot { Id = "r1", Label = "Primary", PathHint = primary });
        var it = new LibraryItem { GogId = 1, Title = "Game", Slug = "game" };
        it.Files.Add(new GameFile { GameGogId = 1, FileKey = "f", Kind = FileKind.Installer,
            RootId = "r1", LocalRelativePath = "setup.exe", LocalSizeBytes = 100, State = FileState.Verified });
        m.Items.Add(it);
        var layout = new BackupLayout(m, primary);

        var job = ReorgPlanner.ForLayoutChange(m, layout);

        Assert.False(job.HasUnavailableDrives, "everything is online -> no false block");
        Assert.NotEmpty(job.Moves);
    }

    [Test]
    void ForFileMoves_OfflineTarget_IsReported()
    {
        var (m, layout, _) = TwoRoots_SecondOffline("Games/game/setup.exe");   // file is fine; target is r2 (offline)

        var job = ReorgPlanner.ForFileMoves(m, layout,
            new[] { (m.Items[0], m.Items[0].Files[0]) }, "r2");

        Assert.True(job.HasUnavailableDrives, "an offline TARGET drive blocks the move");
        Assert.Contains("Archive", string.Join(",", job.UnavailableDrives));
        Assert.Empty(job.Moves);
    }

    [Test]
    void ForFileMoves_OfflineSource_IsReported()
    {
        // File lives on offline r2; we try to move it TO the online primary r1. Source is unreachable.
        var (m, layout, _) = TwoRoots_SecondOffline("Games/game/setup.exe");

        var job = ReorgPlanner.ForFileMoves(m, layout,
            new[] { (m.Items[0], m.Items[0].Files[0]) }, "r1");

        Assert.True(job.HasUnavailableDrives, "an offline SOURCE drive blocks the move");
        Assert.Contains("Archive", string.Join(",", job.UnavailableDrives));
        Assert.Empty(job.Moves);
    }
}
