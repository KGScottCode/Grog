// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Runs;
using Grog.Core.Tests.Framework;
using Grog.Core.Volumes;

// The extras-placement change (09-11). The bug these pin: "Separate folder, on the second drive" wrote the
// routing and planned NOTHING, because the only planner on that screen renames within a root and never reads
// routing. The preview then promised a layout the disk did not have, permanently and silently.
//
// Every case goes through ExtrasPlacementRun.PlanMoves, which is the one place that answers "what has to
// move to make the preview true".
public class ExtrasPlacementTests
{
    // Real dirs, because BackupLayout probes roots on disk and a root it cannot see is "offline" -- which is
    // its own case below and must not silently swallow the others.
    sealed class Bed : IDisposable
    {
        public string Base { get; }
        public string R1 { get; }
        public string R2 { get; }
        public LibraryManifest M { get; }

        public Bed(bool secondOnline = true)
        {
            Base = Directory.CreateTempSubdirectory("grog-extras-").FullName;
            R1 = Path.Combine(Base, "r1");
            R2 = Path.Combine(Base, "r2");
            Directory.CreateDirectory(R1);
            if (secondOnline) Directory.CreateDirectory(R2);
            M = new LibraryManifest { PrimaryRootId = "r1" };
            M.Roots.Add(new BackupRoot { Id = "r1", Label = "Primary", PathHint = R1 });
            M.Roots.Add(new BackupRoot { Id = "r2", Label = "Second", PathHint = R2 });
        }

        /// <summary>One game with one soundtrack extra at <paramref name="rel"/> on <paramref name="rootId"/>,
        /// written to disk so the planners can resolve it.</summary>
        public LibraryManifest WithExtra(string rel, string rootId = "r1")
        {
            var it = new LibraryItem { GogId = 1, Title = "Game", Slug = "game" };
            it.Files.Add(new GameFile
            {
                GameGogId = 1, FileKey = "ost", Kind = FileKind.Extra, Name = "ost.zip", ExtraType = "audio",
                RootId = rootId, LocalRelativePath = rel, LocalSizeBytes = 10, State = FileState.Verified,
            });
            it.Files.Add(new GameFile
            {
                GameGogId = 1, FileKey = "setup", Kind = FileKind.Installer, Name = "setup.exe",
                RootId = "r1", LocalRelativePath = "Games/game/setup.exe", LocalSizeBytes = 20, State = FileState.Verified,
            });
            M.Items.Add(it);

            var root = rootId == "r1" ? R1 : R2;
            var abs = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
            File.WriteAllText(abs, "x");
            var setupAbs = Path.Combine(R1, "Games", "game", "setup.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(setupAbs)!);
            File.WriteAllText(setupAbs, "y");
            return M;
        }

        public BackupLayout Layout() => new(M, R1);
        public void Dispose() { try { Directory.Delete(Base, true); } catch { } }
    }

    // THE BUG. Shape unchanged, storage changed: the old path planned zero moves and reported success.
    [Test]
    void StorageChangeAlone_PlansCrossRootMoves()
    {
        using var bed = new Bed();
        bed.M.ExtrasLayout = ExtrasPlacement.SeparateByGame;
        bed.WithExtra("Extras/game/Soundtracks/ost.zip");

        var job = ExtrasPlacementRun.PlanMoves(bed.M, bed.Layout(), ExtrasPlacement.SeparateByGame, "r2", "r1");

        var mv = Assert.Single(job.Moves, "the extra is planned onto the second device");
        Assert.Equal("r2", mv.ToRootId, "cross-root move, not a same-root rename");
        Assert.Equal(ReorgReason.Relocate, job.Reason, "reason names a device change, for the resume prompt");
    }

    // The shape half still works on its own; it is the half that always did.
    [Test]
    void ShapeChangeAlone_PlansRenamesOnTheSameRoot()
    {
        using var bed = new Bed();
        bed.M.ExtrasLayout = ExtrasPlacement.SeparateByGame;
        bed.WithExtra("game/extras/ost.zip");   // the old flat shape

        var job = ExtrasPlacementRun.PlanMoves(bed.M, bed.Layout(), ExtrasPlacement.SeparateByGame, "r1", "r1");

        var mv = Assert.Single(job.Moves, "the misplaced extra is planned");
        Assert.Null(mv.ToRootId, "same root: a rename, never a copy");
        Assert.Equal("Extras/game/Soundtracks/ost.zip", mv.ToRel, "lands in the chosen shape");
        Assert.Equal(ReorgReason.Layout, job.Reason, "no device changes, so this is a shape job");
    }

    // Both at once is ONE move per file. Planning a file twice would have the second move fail on a source
    // the first had already taken away.
    [Test]
    void ShapeAndStorageTogether_PlanEachFileOnce()
    {
        using var bed = new Bed();
        bed.M.ExtrasLayout = ExtrasPlacement.SeparateByType;
        bed.WithExtra("game/extras/ost.zip");   // wrong shape AND wrong device

        var job = ExtrasPlacementRun.PlanMoves(bed.M, bed.Layout(), ExtrasPlacement.SeparateByType, "r2", "r1");

        Assert.Equal(1, job.Moves.Count, "one file, one move");
        var mv = job.Moves[0];
        Assert.Equal("r2", mv.ToRootId, "it goes to the second device");
        Assert.Equal("Extras/Soundtracks/game/ost.zip", mv.ToRel, "and arrives in the chosen shape, not the old one");
    }

    // WithGame routes an extra with its game, so a stored Extras role root is inert. Planning a device move
    // from it would drag extras away from the games they belong to.
    [Test]
    void WithGame_IgnoresTheStorageChoice()
    {
        using var bed = new Bed();
        bed.M.ExtrasLayout = ExtrasPlacement.WithGame;
        bed.WithExtra("Extras/game/Soundtracks/ost.zip");

        var job = ExtrasPlacementRun.PlanMoves(bed.M, bed.Layout(), ExtrasPlacement.WithGame, "r2", "r1");

        Assert.True(job.Moves.All(mv => mv.ToRootId is null), "no file is sent to another device");
        Assert.True(job.Moves.Any(mv => mv.ToRel == "Games/game/Extras/ost.zip"), "the shape change still happens");
    }

    // (09-19, walk pass 3) Separate tree on the secondary, then back to With Each Game: the shape reverted and
    // the extras STAYED on the secondary while the installers sat on the primary. WithGame brings an extra home.
    [Test]
    void WithGame_BringsAnExtraBackToItsGamesDrive()
    {
        using var bed = new Bed();
        bed.M.ExtrasLayout = ExtrasPlacement.SeparateByType;
        bed.M.Routing.SetRole(ContentRole.Extras, "r2");
        bed.WithExtra("Extras/game/Soundtracks/ost.zip", rootId: "r2");   // installer on r1, extra on r2

        var job = ExtrasPlacementRun.PlanMoves(bed.M, bed.Layout(), ExtrasPlacement.WithGame, "r1", "r1");

        var mv = job.Moves.Single(x => x.FileKey == "ost");
        Assert.Equal("r1", mv.ToRootId, "the extra goes to the drive its installer is on");
        Assert.Equal("Games/game/Extras/ost.zip", mv.ToRel, "in the with-game shape, planned once");
        Assert.False(job.Moves.Any(x => x.FileKey == "setup"), "the installer never moves");
    }

    // Nothing to do is a plan with no moves, not a failure and not a move of zero files.
    [Test]
    void AlreadyCorrect_PlansNothing()
    {
        using var bed = new Bed();
        bed.M.ExtrasLayout = ExtrasPlacement.SeparateByGame;
        bed.WithExtra("Extras/game/Soundtracks/ost.zip");

        var job = ExtrasPlacementRun.PlanMoves(bed.M, bed.Layout(), ExtrasPlacement.SeparateByGame, "r1", "r1");

        Assert.Equal(0, job.Moves.Count, "the disk already matches the setting");
        Assert.False(job.HasUnavailableDrives, "and nothing is blocked");
    }

    // An offline target must block the WHOLE job. A partial move that quietly skips the unreachable files is
    // the failure mode the drive check exists to prevent.
    [Test]
    void OfflineTargetDrive_BlocksTheJob()
    {
        using var bed = new Bed(secondOnline: false);
        bed.M.ExtrasLayout = ExtrasPlacement.SeparateByGame;
        bed.WithExtra("Extras/game/Soundtracks/ost.zip");

        var job = ExtrasPlacementRun.PlanMoves(bed.M, bed.Layout(), ExtrasPlacement.SeparateByGame, "r2", "r1");

        Assert.True(job.HasUnavailableDrives, "the caller is told to reconnect a drive");
        Assert.True(job.UnavailableDrives.Any(d => d.Contains("Second")), "and which one");
    }

    // (09-13) The plan is made against the TARGET, with the manifest left as found. Planned against the
    // committed state it returned the moves that undo the change: WithGame committed, SeparateByType staged,
    // and the card footer said "nothing to move" while Apply then moved everything.
    [Test]
    void PlanMoves_PlansAgainstTheTargetAndLeavesTheManifestUntouched()
    {
        using var bed = new Bed();
        bed.M.ExtrasLayout = ExtrasPlacement.WithGame;   // committed: with the game
        bed.WithExtra("Games/game/Soundtracks/ost.zip");
        bed.M.Routing.SetRole(ContentRole.Extras, "r1");

        var job = ExtrasPlacementRun.PlanMoves(bed.M, bed.Layout(), ExtrasPlacement.SeparateByType, "r1", "r1");

        Assert.Equal(1, job.Moves.Count, "the extra moves into the separate tree");
        Assert.True(job.Moves[0].ToRel.Replace('\\', '/').StartsWith("Extras/", System.StringComparison.Ordinal), $"into Extras/, got {job.Moves[0].ToRel}");
        Assert.Equal(ExtrasPlacement.WithGame, bed.M.ExtrasLayout, "committed shape untouched by a plan");
        Assert.True(bed.M.ExtrasInsideGame, "and its derived flag");
        Assert.Equal("r1", bed.M.Routing.RoleRoots[ContentRole.Extras], "committed routing untouched");
    }

    // (09-13) An offline drive means NOTHING is committed: manifest and preview must not promise a device the
    // extras never reached.
    [Test]
    async Task ApplyAndPlan_CommitsNothingWhenADriveIsOffline()
    {
        using var bed = new Bed(secondOnline: false);
        bed.M.ExtrasLayout = ExtrasPlacement.WithGame;
        bed.WithExtra("Games/game/Soundtracks/ost.zip");
        var store = new MemStore(bed.M);

        var plan = await ExtrasPlacementRun.ApplyAndPlanAsync(store, bed.R1, ExtrasPlacement.SeparateByGame, "r2");

        Assert.True(plan is { HasUnavailableDrives: true }, "the caller is told which drive");
        Assert.Equal(ExtrasPlacement.WithGame, bed.M.ExtrasLayout, "shape NOT committed");
        Assert.False(bed.M.Routing.RoleRoots.TryGetValue(ContentRole.Extras, out var r) && r == "r2", "storage NOT committed");
        Assert.Equal(0, store.Saves, "nothing saved");
    }

    [Test]
    async Task ApplyAndPlan_CommitsBothHalvesWhenItCanRun()
    {
        using var bed = new Bed();
        bed.M.ExtrasLayout = ExtrasPlacement.WithGame;
        bed.WithExtra("Games/game/Soundtracks/ost.zip");
        var store = new MemStore(bed.M);

        var plan = await ExtrasPlacementRun.ApplyAndPlanAsync(store, bed.R1, ExtrasPlacement.SeparateByGame, "r2");

        Assert.True(plan is { HasUnavailableDrives: false } && plan.Count == 1, "one move offered");
        Assert.Equal(ExtrasPlacement.SeparateByGame, bed.M.ExtrasLayout, "shape committed");
        Assert.Equal("r2", bed.M.Routing.RoleRoots[ContentRole.Extras], "storage committed");
        Assert.Equal(1, store.Saves);
    }

    private sealed class MemStore : IManifestStore
    {
        public MemStore(LibraryManifest m) => Current = m;
        public LibraryManifest Current { get; }
        public ManifestGate Gate { get; } = new();
        public int Saves;
        public Task LoadAsync(System.Threading.CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveAsync(System.Threading.CancellationToken ct = default) { Saves++; return Task.CompletedTask; }
    }

    // Sweep 2 #1: "Separate -> Secondary" planned a cross-root move for an extra that was never downloaded;
    // the runner paused on FromAbs "". No bytes, no move.
    [Test]
    void NeverDownloadedExtras_AreNotPlanned()
    {
        using var bed = new Bed();
        bed.M.ExtrasLayout = ExtrasPlacement.SeparateByGame;
        bed.WithExtra("Extras/game/Soundtracks/ost.zip");
        bed.M.Items[0].Files.Add(new GameFile
        {
            GameGogId = 1, FileKey = "manual", Kind = FileKind.Extra, Name = "manual.pdf", ExtraType = "manuals",
            State = FileState.NotBackedUp,
        });

        var job = ExtrasPlacementRun.PlanMoves(bed.M, bed.Layout(), ExtrasPlacement.SeparateByGame, "r2", "r1");

        Assert.True(job.Moves.All(mv => mv.FileKey != "manual"), "an extra with no local bytes is not a move");
        Assert.True(job.Moves.All(mv => !string.IsNullOrEmpty(mv.FromAbs)), "no move without a source");
        Assert.Single(job.Moves, "the downloaded extra still moves");
    }

    // Installers are not the extras choice's business, whatever it is set to.
    [Test]
    void InstallersAreNeverMovedByTheExtrasChoice()
    {
        using var bed = new Bed();
        bed.M.ExtrasLayout = ExtrasPlacement.SeparateByGame;
        bed.WithExtra("Extras/game/Soundtracks/ost.zip");

        var job = ExtrasPlacementRun.PlanMoves(bed.M, bed.Layout(), ExtrasPlacement.SeparateByGame, "r2", "r1");

        Assert.True(job.Moves.All(mv => mv.FileKey != "setup"), "the installer stays where it is");
    }
}
