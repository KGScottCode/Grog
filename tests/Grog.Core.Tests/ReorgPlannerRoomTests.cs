// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Tests.Framework;
using Grog.Core.Volumes;

// A file that cannot fit on the target is never queued (a 13 GB move was queued onto a stick with 4.8 GB free). With a MoveRoom the planner keeps such files out of the plan and
// says why; without one it plans everything as before.
public class ReorgPlannerRoomTests
{
    const long GB = 1_000_000_000L;
    const long FAT32 = 4294967295L;

    // Two online roots; a source path the planner cannot place on the target's volume -> every move is a
    // cross-volume copy (the runner resolves the real source itself).
    sealed class CrossVolumeLayout : IBackupLayout
    {
        private readonly Dictionary<string, string> _roots;
        public CrossVolumeLayout(Dictionary<string, string> roots) { _roots = roots; }
        public string? ResolvePath(GameFile file) => null;
        public string? ResolveTargetDir(long gameGogId, string gameSlug, FileKind kind, out string rootId, GameFile? file = null) { rootId = "r2"; return _roots["r2"]; }
        public string SubPathFor(string gameSlug, FileKind kind, GameFile? file = null, long gogId = 0, string? targetRootId = null) => System.IO.Path.Combine("Games", gameSlug);
        public string? ResolveCloudDir(string gameSlug, out string rootId) { rootId = "r2"; return null; }
        public bool IsOnline(string? rootId) => rootId is not null && _roots.ContainsKey(rootId);
        public string? RootPath(string? rootId) => rootId is not null && _roots.TryGetValue(rootId, out var p) ? p : null;
        public IReadOnlyCollection<string> OnlineRootPaths => _roots.Values;
    }

    static (LibraryManifest m, IBackupLayout layout, List<(LibraryItem, GameFile)> files, string dir) Rig(params long[] sizesBytes)
    {
        var dir = System.IO.Directory.CreateTempSubdirectory("grog-room-").FullName;
        var r1 = System.IO.Path.Combine(dir, "r1"); var r2 = System.IO.Path.Combine(dir, "r2");
        System.IO.Directory.CreateDirectory(r1); System.IO.Directory.CreateDirectory(r2);
        var m = new LibraryManifest { PrimaryRootId = "r1" };
        m.Roots.Add(new BackupRoot { Id = "r1", Label = "Big", PathHint = r1 });
        m.Roots.Add(new BackupRoot { Id = "r2", Label = "Small", PathHint = r2 });
        var files = new List<(LibraryItem, GameFile)>();
        for (var i = 0; i < sizesBytes.Length; i++)
        {
            var it = new LibraryItem { GogId = i + 1, Title = $"Game {i + 1}", Slug = $"game{i + 1}" };
            var f = new GameFile { GameGogId = i + 1, FileKey = $"f{i + 1}", Kind = FileKind.Installer, RootId = "r1",
                LocalRelativePath = $"Games/game{i + 1}/setup{i + 1}.exe", LocalSizeBytes = sizesBytes[i], State = FileState.Verified };
            it.Files.Add(f); m.Items.Add(it); files.Add((it, f));
        }
        var layout = new CrossVolumeLayout(new() { ["r1"] = r1, ["r2"] = r2 });
        return (m, layout, files, dir);
    }

    [Test]
    void Room_WalksInOrder_FreeShrinksPerPlannedMove()
    {
        // 5/3/2 GB onto 6 GB free: 5 GB lands (1 GB left), 3 GB and 2 GB both exceed what is left.
        var (m, layout, files, dir) = Rig(5 * GB, 3 * GB, 2 * GB);
        try
        {
            var job = ReorgPlanner.ForFileMoves(m, layout, files, "r2", new MoveRoom(6 * GB, long.MaxValue));

            Assert.Equal("f1", string.Join(",", job.Moves.Select(x => x.FileKey)), "only what fits, in order, is queued");
            Assert.Equal(2, job.NotFitting.Count);
            Assert.True(job.NotFitting.All(s => s.Reason == MoveRoom.NoRoomReason), "the reason is room, not the per-file limit");
            Assert.Equal("setup2.exe", job.NotFitting[0].FileName);
            Assert.Equal(3 * GB, job.NotFitting[0].SizeBytes);
        }
        finally { System.IO.Directory.Delete(dir, true); }
    }

    [Test]
    void Room_FirstTwoFit_ThirdDoesNot()
    {
        // 2/3/5 GB onto 6 GB free: the first two fit, the third has no room.
        var (m, layout, files, dir) = Rig(2 * GB, 3 * GB, 5 * GB);
        try
        {
            var job = ReorgPlanner.ForFileMoves(m, layout, files, "r2", new MoveRoom(6 * GB, long.MaxValue));

            Assert.Equal("f1,f2", string.Join(",", job.Moves.Select(x => x.FileKey)), "2 + 3 GB fit in 6 GB");
            var skipped = Assert.Single(job.NotFitting);
            Assert.Equal(MoveRoom.NoRoomReason, skipped.Reason);
            Assert.Equal("Game 3", skipped.Title);
            Assert.True(job.Moves.All(x => x.Kind == ReorgMoveKind.CopyVerifyDelete), "rig plans cross-volume copies");
        }
        finally { System.IO.Directory.Delete(dir, true); }
    }

    [Test]
    void Room_OverPerFileLimit_ExcludedEvenWithRoom()
    {
        var (m, layout, files, dir) = Rig(5 * GB);
        try
        {
            var job = ReorgPlanner.ForFileMoves(m, layout, files, "r2", new MoveRoom(100 * GB, FAT32));

            Assert.Empty(job.Moves);
            var skipped = Assert.Single(job.NotFitting);
            Assert.Contains("per-file limit", skipped.Reason);
            Assert.Contains("4", skipped.Reason);
        }
        finally { System.IO.Directory.Delete(dir, true); }
    }

    [Test]
    void Room_Null_PlansEverything()
    {
        var (m, layout, files, dir) = Rig(5 * GB, 3 * GB, 2 * GB);
        try
        {
            var job = ReorgPlanner.ForFileMoves(m, layout, files, "r2");

            Assert.Equal(3, job.Moves.Count, "no room given -> no check, as before");
            Assert.Empty(job.NotFitting);
        }
        finally { System.IO.Directory.Delete(dir, true); }
    }

    [Test]
    void Room_SameVolumeRename_IgnoresRoom()
    {
        // Real layout, two folders on one volume: the planner sees the same path root and plans renames.
        var dir = System.IO.Directory.CreateTempSubdirectory("grog-room-rn-").FullName;
        var r1 = System.IO.Path.Combine(dir, "r1"); var r2 = System.IO.Path.Combine(dir, "r2");
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(r1, "Games", "game")); System.IO.Directory.CreateDirectory(r2);
        System.IO.File.WriteAllText(System.IO.Path.Combine(r1, "Games", "game", "setup.exe"), "x");
        var m = new LibraryManifest { PrimaryRootId = "r1" };
        m.Roots.Add(new BackupRoot { Id = "r1", Label = "One", PathHint = r1 });
        m.Roots.Add(new BackupRoot { Id = "r2", Label = "Two", PathHint = r2 });
        var it = new LibraryItem { GogId = 1, Title = "Game", Slug = "game" };
        var f = new GameFile { GameGogId = 1, FileKey = "f", Kind = FileKind.Installer, RootId = "r1",
            LocalRelativePath = "Games/game/setup.exe", LocalSizeBytes = 5 * GB, State = FileState.Verified };
        it.Files.Add(f); m.Items.Add(it);
        try
        {
            var layout = new BackupLayout(m, r1);
            var job = ReorgPlanner.ForFileMoves(m, layout, new[] { (it, f) }, "r2", new MoveRoom(0, 1));

            var mv = Assert.Single(job.Moves);
            Assert.Equal(ReorgMoveKind.Rename, mv.Kind);
            Assert.Empty(job.NotFitting, "a rename writes nothing: room and the per-file limit do not apply");
        }
        finally { System.IO.Directory.Delete(dir, true); }
    }

    // 1277: a resumed job is re-checked; a pending file the drive can no longer take is failed with the reason, in order.
    [Test]
    void Resume_DropsPendingMovesThatNoLongerFit_InOrder()
    {
        var job = new ReorgJob { Reason = ReorgReason.Relocate };
        ReorgMove Mv(string name, long size, ReorgMoveState st = ReorgMoveState.Pending) => new()
        { GogId = 1, FileKey = name, Title = "T", ToAbs = "/t/" + name, FromAbs = "/s/" + name, SizeBytes = size, State = st, Kind = ReorgMoveKind.CopyVerifyDelete, ToRootId = "b", FromRootId = "a" };
        job.Moves.Add(Mv("done.bin", 4_000_000_000, ReorgMoveState.Done));
        job.Moves.Add(Mv("big.sh", 6_500_000_000));       // over the 4 GiB limit
        job.Moves.Add(Mv("mid.bin", 2_300_000_000));      // over the room
        job.Moves.Add(Mv("patch.exe", 27_000_000));       // fits
        int dropped = ReorgPlanner.DropWhatNoLongerFits(job, new MoveRoom(FreeBytes: 700_000_000, MaxFileBytes: 4_294_967_295));
        Assert.Equal(2, dropped, "two cannot fit");
        // (1283) Dropped moves leave the plan: marked Failed they were re-admitted and retried into a full drive.
        Assert.Equal("done.bin,patch.exe", string.Join(",", job.Moves.Select(x => x.FileKey)), "the two that cannot fit are out of the plan");
        Assert.Equal(ReorgMoveState.Pending, job.Moves[1].State, "the small one still moves");
        Assert.Equal(ReorgMoveState.Done, job.Moves[0].State, "landed files are untouched");
        Assert.Equal(2, job.NotFitting.Count, "both listed for the user");
    }

    private sealed class PathsOnly : IBackupLayout
    {
        private readonly Dictionary<string, string?> _paths;
        public PathsOnly(Dictionary<string, string?> paths) => _paths = paths;
        public string? ResolvePath(GameFile f) => null;
        public string? ResolveTargetDir(long gogId, string slug, FileKind kind, out string rootId, GameFile? f = null) { rootId = "a"; return null; }
        public string? RootPath(string? rootId) => rootId is not null && _paths.TryGetValue(rootId, out var p) ? p : null;
        public IReadOnlyCollection<string> OnlineRootPaths => _paths.Values.Where(v => v is not null).Select(v => v!).ToList();
        public bool IsOnline(string? rootId) => RootPath(rootId) is not null;
        public string SubPathFor(string slug, FileKind kind, GameFile? f = null, long gogId = 0, string? rootId = null) => "";
        public string? ResolveCloudDir(string slug, out string rootId) { rootId = "a"; return null; }
    }

    // 1283 (QA 09-30 B3): a journal's absolute paths follow the roots to where they are now (a drive back under
    // another letter); a root the layout cannot place keeps the journal's path; settled moves are left alone.
    [Test]
    public void Rebase_follows_the_roots_current_paths()
    {
        var m = new LibraryManifest { PrimaryRootId = "a" };
        m.Roots.Add(new BackupRoot { Id = "a" }); m.Roots.Add(new BackupRoot { Id = "b" });
        var item = new LibraryItem { GogId = 1, Title = "T" };
        item.Files.Add(new GameFile { GameGogId = 1, FileKey = "x", RootId = "a", LocalRelativePath = "Games/t/x.exe" });
        m.Items.Add(item);
        var job = new ReorgJob { Reason = ReorgReason.Relocate };
        var sep = System.IO.Path.DirectorySeparatorChar;
        job.Moves.Add(new ReorgMove { GogId = 1, FileKey = "x", FromRootId = "a", ToRootId = "b", FromRel = "Games/t/x.exe", ToRel = "Games/t/x.exe",
                                      FromAbs = $"E:{sep}old{sep}Games{sep}t{sep}x.exe", ToAbs = $"F:{sep}old{sep}Games{sep}t{sep}x.exe", Kind = ReorgMoveKind.CopyVerifyDelete });
        job.Moves.Add(new ReorgMove { GogId = 1, FileKey = "done", FromRootId = "a", ToRootId = "b", FromRel = "d", ToRel = "d", FromAbs = "E:old", ToAbs = "F:old", State = ReorgMoveState.Done });
        var newA = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "grog-rebase-a"));
        int changed = ReorgPlanner.Rebase(job, m, new PathsOnly(new() { ["a"] = newA, ["b"] = null }));
        Assert.Equal(1, changed, "the source followed its root; the target's root is not placed; the settled move is untouched");
        Assert.Equal(System.IO.Path.Combine(newA, "Games", "t", "x.exe"), job.Moves[0].FromAbs);
        Assert.Equal($"F:{sep}old{sep}Games{sep}t{sep}x.exe", job.Moves[0].ToAbs, "kept: root b is offline");
        Assert.Equal("E:old", job.Moves[1].FromAbs, "settled: untouched");
    }

    // 1283 (QA 09-30 B2): a Change Folder job carries its follow-up (which root flips to which path) in the journal.
    [Test]
    public void ForRootPathChange_records_the_repoint_and_the_journal_keeps_it()
    {
        var m = new LibraryManifest { PrimaryRootId = "a" };
        m.Roots.Add(new BackupRoot { Id = "a" });
        var item = new LibraryItem { GogId = 1, Title = "T" };
        item.Files.Add(new GameFile { GameGogId = 1, FileKey = "x", RootId = "a", LocalRelativePath = "Games/t/x.exe", State = FileState.Verified, LocalSizeBytes = 5 });
        m.Items.Add(item);
        var newPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "grog-repoint-new"));
        var job = ReorgPlanner.ForRootPathChange(m, "a", System.IO.Path.GetTempPath(), newPath);
        Assert.Equal("a", job.RepointRootId); Assert.Equal(newPath, job.RepointPath);
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "grog-repoint-" + Guid.NewGuid().ToString("N")[..8]);
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            var store = new ReorgJournalStore(dir); store.Save(job);
            var back = store.Load()!;
            Assert.Equal("a", back.RepointRootId); Assert.Equal(newPath, back.RepointPath);
        }
        finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
    }
}
