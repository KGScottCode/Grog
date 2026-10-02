// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System;
using System.IO;
using System.Threading;
using System.Linq;
using System.Threading.Tasks;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Storage;
using Grog.Core.Tests.Framework;
using Grog.Core.Verify;
using Grog.Core.Volumes;

[NewBatch]
[Trait("reorg")]
public class ReorgRunnerTests
{
    // ---- harness ---------------------------------------------------------------------------

    sealed record Env(JsonManifestStore Store, string Root, BackupLayout Layout, string ConfigDir);

    // A file can land in the tree while the recursive delete walks it (a journal .bak, a marker); one retry covers it
    // without hiding a real leak: the third failure still throws.
    static void RemoveTree(string root)
    {
        for (int i = 0; ; i++)
        {
            try { Directory.Delete(root, true); return; }
            catch (IOException) when (i < 2) { Thread.Sleep(150); }
        }
    }

    static Env NewSetup()
    {
        var root = Directory.CreateTempSubdirectory("grog-reorg-").FullName;
        var cfg = Path.Combine(root, "_cfg");
        Environment.SetEnvironmentVariable("GROG_CONFIG_DIR", cfg);
        var paths = GrogPaths.Resolve(root);
        var store = new JsonManifestStore(paths);
        store.LoadAsync().GetAwaiter().GetResult();
        var layout = new BackupLayout(store.Current, root);   // registers the primary root
        return new Env(store, root, layout, paths.ConfigDir);
    }

    static GameFile AddFileOnDisk(Env e, long gogId, string slug, string title, FileKind kind, string rel, string content)
    {
        var item = e.Store.Current.Items.FirstOrDefault(i => i.GogId == gogId);
        if (item is null)
        {
            item = new LibraryItem { GogId = gogId, Title = title, Slug = slug };
            e.Store.Current.Items.Add(item);
        }
        var abs = Path.Combine(e.Root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllText(abs, content);
        var f = new GameFile
        {
            GameGogId = gogId, FileKey = $"{slug}/{Path.GetFileName(rel)}", Kind = kind, Name = Path.GetFileName(rel),
            RootId = e.Store.Current.PrimaryRootId, LocalRelativePath = rel,
            State = FileState.Present, ExpectedSizeBytes = content.Length, LocalSizeBytes = content.Length,
        };
        item.Files.Add(f);
        return f;
    }

    // ---- the breaker: a drive that goes away pauses the job instead of failing every move ----------

    [Test]
    async Task DestinationGone_PausesWithReason_AndResumeRetries()
    {
        var e = NewSetup();
        try
        {
            for (int i = 0; i < 4; i++)
                AddFileOnDisk(e, 1, "mover", "Mover", FileKind.Installer, $"mover/part{i}.bin", "payload" + i);
            e.Store.Current.SetExtrasLayout(Grog.Core.Models.ExtrasPlacement.SeparateByGame);
            var job = ReorgPlanner.ForLayoutChange(e.Store.Current, e.Layout);
            Assert.Equal(4, job.Total);

            // The destination volume "unplugs" after the first move lands.
            int done = 0;
            var runner = new ReorgRunner(e.Store, new ReorgJournalStore(e.ConfigDir));
            runner.Progress += p => done = p.FilesDone;
            runner.VolumeReachable = _ => done < 1;   // the probe gets the STORAGE folder (1283), so it is the drive, not a file path

            var res = await runner.RunAsync(job, new ReorgControl());
            Assert.Equal(ReorgOutcome.Paused, res.Outcome, "the runner paused itself");
            Assert.True(res.PauseReason!.Contains("destination"), res.PauseReason);
            Assert.Equal(job.Pending()[0].ToRootId ?? job.Pending()[0].FromRootId, res.PausedForRootId, "the host knows which drive to wait for (walk 5.6)");
            Assert.Equal(1, res.Moved, "one landed before the drive went");
            Assert.Equal(0, res.Failed, "the tripping move went back to Pending, nothing was burned through");
            Assert.Equal(3, job.Pending().Count, "three still waiting, none Failed");
            Assert.True(job.Paused, "pause persisted for the resume prompt");

            // Drive back: Resume finishes the job.
            runner.VolumeReachable = _ => true;
            var again = await runner.RunAsync(job, new ReorgControl());
            Assert.Equal(ReorgOutcome.Completed, again.Outcome);
            Assert.Equal(3, again.Moved);
        }
        finally { RemoveTree(e.Root); }
    }

    [Test]
    async Task FailedMoves_AreReadmittedOnResume()
    {
        var job = JobWith(("a", ReorgMoveState.Done), ("b", ReorgMoveState.Failed), ("c", ReorgMoveState.Failed));
        job.Moves[1].Error = "disk full";
        var e = NewSetup();
        try
        {
            foreach (var mv in job.Moves) { mv.FromAbs = Path.Combine(e.Root, "old", mv.FileKey); mv.ToAbs = Path.Combine(e.Root, "new", mv.FileKey); }
            // Nothing on disk for b/c and the source volume is reachable: they settle as Skipped (repath), which
            // is enough to prove they were picked up again rather than left Failed forever.
            var res = await new ReorgRunner(e.Store, new ReorgJournalStore(e.ConfigDir)).RunAsync(job, new ReorgControl());
            Assert.Equal(0, job.Moves.Count(m => m.State == ReorgMoveState.Failed), "no Failed left behind");
            Assert.Equal(ReorgOutcome.Completed, res.Outcome);
        }
        finally { RemoveTree(e.Root); }
    }

    [Test]
    async Task SourceVolumeGone_FailsInsteadOfRepathing()
    {
        var e = NewSetup();
        try
        {
            var f = AddFileOnDisk(e, 1, "mover", "Mover", FileKind.Installer, "mover/setup.exe", "payload");
            e.Store.Current.SetExtrasLayout(Grog.Core.Models.ExtrasPlacement.SeparateByGame);
            var job = ReorgPlanner.ForLayoutChange(e.Store.Current, e.Layout);
            File.Delete(Path.Combine(e.Root, "mover", "setup.exe"));   // "the drive is gone": file not found
            var runner = new ReorgRunner(e.Store, new ReorgJournalStore(e.ConfigDir)) { VolumeReachable = _ => false };
            var res = await runner.RunAsync(job, new ReorgControl());
            Assert.Equal(ReorgOutcome.Paused, res.Outcome, "a missing source VOLUME pauses; it is not 'never downloaded'");
            Assert.Equal("mover/setup.exe", f.LocalRelativePath, "manifest NOT repathed to a place the file never went");
        }
        finally { RemoveTree(e.Root); }
    }

    // ---- same-drive (rename) ---------------------------------------------------------------

    [Test]
    async Task SameDrive_Rename_MovesFileAndRepaths()
    {
        var e = NewSetup();
        try
        {
            AddFileOnDisk(e, 1, "mover", "Mover", FileKind.Installer, "mover/setup.exe", "payload");
            e.Store.Current.SetExtrasLayout(Grog.Core.Models.ExtrasPlacement.SeparateByGame);

            var job = ReorgPlanner.ForLayoutChange(e.Store.Current, e.Layout);
            Assert.Equal(1, job.Total, "one file needs moving (flat -> Games)");
            Assert.Equal(ReorgMoveKind.Rename, job.Moves[0].Kind, "same root -> rename");

            var runner = new ReorgRunner(e.Store, new ReorgJournalStore(e.ConfigDir));
            var res = await runner.RunAsync(job, new ReorgControl());

            Assert.Equal(ReorgOutcome.Completed, res.Outcome, "completed");
            Assert.False(File.Exists(Path.Combine(e.Root, "mover", "setup.exe")), "old path gone");
            Assert.True(File.Exists(Path.Combine(e.Root, "Games", "mover", "setup.exe")), "at new path");
            Assert.Equal("Games/mover/setup.exe", e.Store.Current.Items[0].Files[0].LocalRelativePath, "manifest repathed");
        }
        finally { RemoveTree(e.Root); }
    }

    // ---- cross-drive (copy -> verify -> delete) --------------------------------------------

    static async Task<ReorgMove> CrossDriveMove(Env e, string root2, string content, string? md5Override = null)
    {
        var f = AddFileOnDisk(e, 2, "archived", "Archived", FileKind.Installer, "archived/big.bin", content);
        var fromAbs = Path.Combine(e.Root, "archived", "big.bin");
        var toAbs = Path.Combine(root2, "Games", "archived", "big.bin");
        return new ReorgMove
        {
            GogId = 2, FileKey = f.FileKey, Title = "Archived",
            FromRel = "archived/big.bin", ToRel = "Games/archived/big.bin",
            ToRootId = "root2-id", FromAbs = fromAbs, ToAbs = toAbs,
            SizeBytes = content.Length,
            ExpectedMd5 = md5Override ?? await Hashing.Md5Async(fromAbs),
            Kind = ReorgMoveKind.CopyVerifyDelete,
        };
    }

    [Test]
    async Task CrossDrive_CopyVerifyDelete_MovesAndDeletesSource()
    {
        var e = NewSetup();
        var root2 = Directory.CreateTempSubdirectory("grog-reorg2-").FullName;
        try
        {
            var mv = await CrossDriveMove(e, root2, "the-payload-bytes");
            var job = new ReorgJob { Reason = ReorgReason.ChangeFolder, Moves = { mv } };

            var runner = new ReorgRunner(e.Store, new ReorgJournalStore(e.ConfigDir));
            var res = await runner.RunAsync(job, new ReorgControl());

            Assert.Equal(ReorgOutcome.Completed, res.Outcome, "completed");
            Assert.True(File.Exists(mv.ToAbs), "copied to destination");
            Assert.False(File.Exists(mv.FromAbs), "source deleted only after verify");
            Assert.False(File.Exists(mv.ToAbs + ".grogpart"), "no leftover .part");
            Assert.Equal("root2-id", e.Store.Current.Items[0].Files[0].RootId, "root id repathed");
            Assert.Equal("Games/archived/big.bin", e.Store.Current.Items[0].Files[0].LocalRelativePath, "relative path repathed");
            Assert.False(Directory.Exists(Path.GetDirectoryName(mv.FromAbs)), "the emptied source game folder is pruned, as a rename's is (owner 09-04)");
        }
        finally { Directory.Delete(e.Root, true); Directory.Delete(root2, true); }
    }

    [Test]
    async Task CrossDrive_PrunesPerGameExtrasFolder_ButNeverTopLevelCategories()
    {
        // Extras-with-each-game: Games/<slug>/Extras/<file>. Moving the last extra off must remove Extras/ AND
        // the emptied <slug>/ (owner 09-04: the walk stopped on the name "Extras" and left every game shell).
        var e = NewSetup();
        var root2 = Directory.CreateTempSubdirectory("grog-reorg2-").FullName;
        try
        {
            var rel = "Games/lonely/Extras/manual.zip";
            var f = AddFileOnDisk(e, 7, "lonely", "Lonely", FileKind.Extra, rel, "pdf-bytes");
            var fromAbs = Path.Combine(e.Root, "Games", "lonely", "Extras", "manual.zip");
            var mv = new ReorgMove
            {
                GogId = 7, FileKey = f.FileKey, Title = "Lonely", FromRel = rel, ToRel = rel, ToRootId = "root2-id",
                FromAbs = fromAbs, ToAbs = Path.Combine(root2, "Games", "lonely", "Extras", "manual.zip"),
                SizeBytes = 9, ExpectedMd5 = await Hashing.Md5Async(fromAbs), Kind = ReorgMoveKind.CopyVerifyDelete,
            };
            var job = new ReorgJob { Reason = ReorgReason.Relocate, Moves = { mv } };
            var res = await new ReorgRunner(e.Store, new ReorgJournalStore(e.ConfigDir)).RunAsync(job, new ReorgControl());
            Assert.Equal(ReorgOutcome.Completed, res.Outcome);
            Assert.False(Directory.Exists(Path.Combine(e.Root, "Games", "lonely")), "the game shell (and its Extras/) is gone");
            Assert.True(Directory.Exists(Path.Combine(e.Root, "Games")), "top-level Games/ stays");
        }
        finally { Directory.Delete(e.Root, true); Directory.Delete(root2, true); }
    }

    [Test]
    async Task LayoutChange_PrunesEmptiedSourceFolders()
    {
        // Owner-hit 2026-08-30 (mac, but platform-agnostic): switching layouts moved every file and left
        // the old game/bucket folder chain behind as empty husks. The runner now prunes what it emptied -
        // and ONLY that: the category root stays, and a folder still holding anything is untouched.
        var e = NewSetup();
        try
        {
            AddFileOnDisk(e, 5, "wonders", "Age of Wonders", FileKind.Extra,
                "Extras/wonders/Art/artbook.zip", "art-bytes");
            e.Store.Current.Items.Single(i => i.GogId == 5).Files.Single().ExtraType = "artworks";
            e.Store.Current.SetExtrasLayout(ExtrasPlacement.SeparateByType);   // -> Extras/Art/wonders/

            var job = ReorgPlanner.ForLayoutChange(e.Store.Current, e.Layout);
            Assert.Equal(1, job.Total, "the extra needs moving");
            var res = await new ReorgRunner(e.Store, new ReorgJournalStore(e.ConfigDir))
                .RunAsync(job, new ReorgControl());

            Assert.Equal(ReorgOutcome.Completed, res.Outcome, "completed");
            Assert.True(File.Exists(Path.Combine(e.Root, "Extras", "Art", "wonders", "artbook.zip")), "moved to type-first");
            Assert.True(!Directory.Exists(Path.Combine(e.Root, "Extras", "wonders")), "emptied game folder pruned (with its Art/)");
            Assert.True(Directory.Exists(Path.Combine(e.Root, "Extras")), "category root NEVER pruned");
        }
        finally { RemoveTree(e.Root); }
    }

    [Test]
    void Journal_Save_OverwritesAReadOnlyJournal()
    {
        // Owner-hit 2026-08-30: a reorg.json carrying the read-only attribute made the atomic replace
        // throw UnauthorizedAccessException and crashed the run. Save must clear the attribute and win.
        var dir = Directory.CreateTempSubdirectory("grog-journal-").FullName;
        try
        {
            var store = new ReorgJournalStore(dir);
            store.Save(new ReorgJob { Reason = ReorgReason.Layout });
            var path = Path.Combine(dir, ReorgJournalStore.FileName);
            File.SetAttributes(path, FileAttributes.ReadOnly);

            store.Save(new ReorgJob { Reason = ReorgReason.ChangeFolder });   // must not throw

            var back = store.Load();
            Assert.Equal(ReorgReason.ChangeFolder, back!.Reason, "second save landed over the read-only file");
        }
        finally
        {
            var p = Path.Combine(dir, ReorgJournalStore.FileName);
            if (File.Exists(p)) File.SetAttributes(p, FileAttributes.Normal);
            Directory.Delete(dir, true);
        }
    }

    [Test]
    async Task CrossDrive_PauseMidCopy_StopsWithinTheFile()
    {
        var e = NewSetup();
        var root2 = Directory.CreateTempSubdirectory("grog-reorg2-").FullName;
        try
        {
            // > 1 MB so the copy takes multiple chunks; pause fires from the first mid-file progress beat.
            var mv = await CrossDriveMove(e, root2, new string('x', 3 * 1024 * 1024));
            var job = new ReorgJob { Reason = ReorgReason.ChangeFolder, Moves = { mv } };

            var runner = new ReorgRunner(e.Store, new ReorgJournalStore(e.ConfigDir));
            var control = new ReorgControl();
            runner.Progress += p => { if (p.CurrentFileFraction is > 0 and < 1) control.Pause(); };
            var res = await runner.RunAsync(job, control);

            Assert.Equal(ReorgOutcome.Paused, res.Outcome, "pause honored MID-copy, not after the file");
            Assert.True(File.Exists(mv.FromAbs), "source untouched");
            Assert.False(File.Exists(mv.ToAbs), "nothing promoted");
            Assert.True(mv.State is ReorgMoveState.Pending, "move un-settled, resume re-runs it");
            Assert.True(job.Paused, "journal records the pause for restart");
        }
        finally { Directory.Delete(e.Root, true); Directory.Delete(root2, true); }
    }

    [Test]
    async Task CrossDrive_VerifyFailure_LeavesSourceUntouched()
    {
        var e = NewSetup();
        var root2 = Directory.CreateTempSubdirectory("grog-reorg2-").FullName;
        try
        {
            var mv = await CrossDriveMove(e, root2, "content-here", md5Override: "00000000000000000000000000000000");
            var job = new ReorgJob { Reason = ReorgReason.ChangeFolder, Moves = { mv } };

            var runner = new ReorgRunner(e.Store, new ReorgJournalStore(e.ConfigDir));
            var res = await runner.RunAsync(job, new ReorgControl());

            Assert.Equal(ReorgOutcome.CompletedWithErrors, res.Outcome, "bad checksum -> errors");
            Assert.Equal(ReorgMoveState.Failed, mv.State, "move failed");
            Assert.True(File.Exists(mv.FromAbs), "source NOT deleted on verify failure");
            Assert.False(File.Exists(mv.ToAbs), "no promoted destination");
            Assert.False(File.Exists(mv.ToAbs + ".grogpart"), "partial cleaned up");
            Assert.Equal("archived/big.bin", e.Store.Current.Items[0].Files[0].LocalRelativePath, "manifest not repathed");
        }
        finally { Directory.Delete(e.Root, true); Directory.Delete(root2, true); }
    }

    // ---- missing source --------------------------------------------------------------------

    [Test]
    async Task MissingSource_IsSkippedButRepathed()
    {
        var e = NewSetup();
        try
        {
            // Manifest says a file exists in the flat layout, but nothing is on disk.
            var item = new LibraryItem { GogId = 3, Title = "Ghost", Slug = "ghost" };
            item.Files.Add(new GameFile
            {
                GameGogId = 3, FileKey = "ghost/x.bin", Kind = FileKind.Installer,
                RootId = e.Store.Current.PrimaryRootId, LocalRelativePath = "ghost/x.bin",
                State = FileState.Missing,
            });
            e.Store.Current.Items.Add(item);

            var job = ReorgPlanner.ForLayoutChange(e.Store.Current, e.Layout);
            var runner = new ReorgRunner(e.Store, new ReorgJournalStore(e.ConfigDir));
            var res = await runner.RunAsync(job, new ReorgControl());

            Assert.Equal(1, res.Skipped, "one skipped (no bytes on disk)");
            Assert.Equal("Games/ghost/x.bin", item.Files[0].LocalRelativePath, "still repathed for future writes");
        }
        finally { RemoveTree(e.Root); }
    }

    // ---- idempotent re-run -----------------------------------------------------------------

    [Test]
    async Task Rerun_OfCompletedJob_IsNoOp()
    {
        var e = NewSetup();
        try
        {
            AddFileOnDisk(e, 1, "mover", "Mover", FileKind.Installer, "mover/setup.exe", "payload");
            var job = ReorgPlanner.ForLayoutChange(e.Store.Current, e.Layout);
            var runner = new ReorgRunner(e.Store, new ReorgJournalStore(e.ConfigDir));

            await runner.RunAsync(job, new ReorgControl());
            var res2 = await runner.RunAsync(job, new ReorgControl());   // run again

            Assert.Equal(ReorgOutcome.Completed, res2.Outcome, "second run completes cleanly");
            Assert.Equal(0, res2.Moved, "nothing left to move");
            Assert.True(File.Exists(Path.Combine(e.Root, "Games", "mover", "setup.exe")), "file still in place");
        }
        finally { RemoveTree(e.Root); }
    }

    // ---- pause + resume --------------------------------------------------------------------

    [Test]
    async Task Pause_KeepsJournal_ResumeFinishes()
    {
        var e = NewSetup();
        try
        {
            AddFileOnDisk(e, 1, "a", "A", FileKind.Installer, "a/s.exe", "aaa");
            AddFileOnDisk(e, 2, "b", "B", FileKind.Installer, "b/s.exe", "bbb");
            var job = ReorgPlanner.ForLayoutChange(e.Store.Current, e.Layout);
            Assert.Equal(2, job.Total, "two moves planned");

            var journal = new ReorgJournalStore(e.ConfigDir);
            var runner = new ReorgRunner(e.Store, journal);
            var control = new ReorgControl();
            runner.Progress += p => { if (p.FilesDone >= 1) control.Pause(); };   // pause after first settles

            var res1 = await runner.RunAsync(job, control);
            Assert.Equal(ReorgOutcome.Paused, res1.Outcome, "paused mid-plan");
            Assert.True(journal.Exists, "journal kept for resume");

            var pending = runner.LoadPending();
            Assert.NotNull(pending);
            Assert.Equal(1, pending!.Settled, "one already settled");

            var res2 = await runner.RunAsync(pending, new ReorgControl());   // resume
            Assert.Equal(ReorgOutcome.Completed, res2.Outcome, "resume completes");
            Assert.False(journal.Exists, "journal cleared on completion");
            Assert.True(File.Exists(Path.Combine(e.Root, "Games", "a", "s.exe")), "first moved");
            Assert.True(File.Exists(Path.Combine(e.Root, "Games", "b", "s.exe")), "second moved on resume");
        }
        finally { RemoveTree(e.Root); }
    }

    [Test]
    async Task Pause_IsRecordedInTheJournal_AndClearedOnResume()
    {
        // ReorgJob.Paused was persisted and READ (MainWindowViewModel.MoveQueuePaused, ActivityMap's
        // movePaused) but never WRITTEN, so a paused job looked in-progress on disk and FileActivity.MovePaused
        // was unreachable. Downloads already survive a restart as paused; moves now do too.
        var e = NewSetup();
        try
        {
            AddFileOnDisk(e, 1, "a", "A", FileKind.Installer, "a/s.exe", "aaa");
            AddFileOnDisk(e, 2, "b", "B", FileKind.Installer, "b/s.exe", "bbb");
            var job = ReorgPlanner.ForLayoutChange(e.Store.Current, e.Layout);

            var journal = new ReorgJournalStore(e.ConfigDir);
            var runner = new ReorgRunner(e.Store, journal);
            var control = new ReorgControl();
            runner.Progress += p => { if (p.FilesDone >= 1) control.Pause(); };

            await runner.RunAsync(job, control);

            // Read it back from DISK, not from the in-memory job: surviving the process is the whole point.
            var reloaded = runner.LoadPending();
            Assert.NotNull(reloaded);
            Assert.True(reloaded!.Paused, "a paused job says so in its journal");

            // Resuming clears it, so a job that is running never reads back as paused.
            var resumed = await runner.RunAsync(reloaded, new ReorgControl());
            Assert.Equal(ReorgOutcome.Completed, resumed.Outcome, "resume completes");
            Assert.False(reloaded.Paused, "pause flag cleared once running again");
        }
        finally { RemoveTree(e.Root); }
    }

    // ---- stop: revert or accept orphans ------------------------------------------------------

    [Test]
    async Task Cancel_KeepsTheJournal_SoTheMoveCanStillBeUndone()
    {
        // Canceling used to DELETE the journal. The files already moved were real and no longer where the
        // manifest expected them, and with the plan gone there was no way to put them back or even list them.
        var e = NewSetup();
        try
        {
            AddFileOnDisk(e, 1, "a", "A", FileKind.Installer, "a/s.exe", "aaa");
            AddFileOnDisk(e, 2, "b", "B", FileKind.Installer, "b/s.exe", "bbb");
            var job = ReorgPlanner.ForLayoutChange(e.Store.Current, e.Layout);

            var journal = new ReorgJournalStore(e.ConfigDir);
            var runner = new ReorgRunner(e.Store, journal);
            var control = new ReorgControl();
            runner.Progress += p => { if (p.FilesDone >= 1) control.Cancel(); };

            var res = await runner.RunAsync(job, control);
            Assert.Equal(ReorgOutcome.Canceled, res.Outcome, "canceled mid-plan");
            Assert.True(journal.Exists, "journal kept so the stop can still be resolved");

            var reloaded = runner.LoadPending();
            Assert.NotNull(reloaded);
            Assert.True(reloaded!.Canceled, "reloads as canceled, not as merely paused");
        }
        finally { RemoveTree(e.Root); }
    }

    [Test]
    async Task Revert_PutsMovedFilesBack_AndRepathsTheManifest()
    {
        var e = NewSetup();
        try
        {
            var f1 = AddFileOnDisk(e, 1, "a", "A", FileKind.Installer, "a/s.exe", "aaa");
            AddFileOnDisk(e, 2, "b", "B", FileKind.Installer, "b/s.exe", "bbb");
            var beforeRel = f1.LocalRelativePath;
            var beforeAbs = Path.Combine(e.Root, "a", "s.exe");

            var journal = new ReorgJournalStore(e.ConfigDir);
            var runner = new ReorgRunner(e.Store, journal);
            var job = ReorgPlanner.ForLayoutChange(e.Store.Current, e.Layout);
            await runner.RunAsync(job, new ReorgControl());
            Assert.False(File.Exists(beforeAbs), "moved away by the run");

            var revert = await runner.RevertAsync(job);

            Assert.Equal(2, revert.Restored, "both files put back");
            Assert.True(revert.Clean, "nothing failed or went missing");
            Assert.True(File.Exists(beforeAbs), "file is back at its original path");
            Assert.False(File.Exists(Path.Combine(e.Root, "Games", "a", "s.exe")), "and gone from the destination");
            Assert.Equal(beforeRel, f1.LocalRelativePath, "manifest points at the original path again");
            Assert.False(journal.Exists, "journal cleared -- nothing left to resolve");
        }
        finally { RemoveTree(e.Root); }
    }

    [Test]
    async Task Revert_CountsFilesItCannotPutBack_InsteadOfFailingTheWholeUndo()
    {
        // A destination file deleted by hand (or on a drive that was pulled) cannot go back. That is the
        // user's to own -- but it must not stop the OTHER files from being restored.
        var e = NewSetup();
        try
        {
            AddFileOnDisk(e, 1, "a", "A", FileKind.Installer, "a/s.exe", "aaa");
            AddFileOnDisk(e, 2, "b", "B", FileKind.Installer, "b/s.exe", "bbb");

            var journal = new ReorgJournalStore(e.ConfigDir);
            var runner = new ReorgRunner(e.Store, journal);
            var job = ReorgPlanner.ForLayoutChange(e.Store.Current, e.Layout);
            await runner.RunAsync(job, new ReorgControl());

            File.Delete(job.Moves[0].ToAbs);   // vanished from under us

            var revert = await runner.RevertAsync(job);
            Assert.Equal(1, revert.Unrecoverable, "the vanished one is reported, not silently dropped");
            Assert.Equal(1, revert.Restored, "the other still goes back");
            Assert.False(revert.Clean, "caller can tell the user it was not a clean undo");
        }
        finally { RemoveTree(e.Root); }
    }

    // ---- cancel ----------------------------------------------------------------------------

    [Test]
    async Task Cancel_StopsAndAbandonsRemaining_CompletedStayMoved()
    {
        var e = NewSetup();
        try
        {
            AddFileOnDisk(e, 1, "a", "A", FileKind.Installer, "a/s.exe", "aaa");
            AddFileOnDisk(e, 2, "b", "B", FileKind.Installer, "b/s.exe", "bbb");
            var job = ReorgPlanner.ForLayoutChange(e.Store.Current, e.Layout);

            var journal = new ReorgJournalStore(e.ConfigDir);
            var runner = new ReorgRunner(e.Store, journal);
            var control = new ReorgControl();
            runner.Progress += p => { if (p.FilesDone >= 1) control.Cancel(); };

            var res = await runner.RunAsync(job, control);
            Assert.Equal(ReorgOutcome.Canceled, res.Outcome, "canceled");
            // Was: "journal deleted on cancel (no resume)". Cancel now KEEPS the plan so the already-moved
            // files can still be put back or knowingly accepted as orphans -- see
            // Cancel_KeepsTheJournal_SoTheMoveCanStillBeUndone.
            Assert.True(journal.Exists, "journal kept on cancel so the stop can be resolved");
            Assert.Equal(1, job.Moves.Count(m => m.State == ReorgMoveState.Done), "first move stays done");
            Assert.Equal(1, job.Moves.Count(m => m.State == ReorgMoveState.Pending), "second left pending, untouched");
        }
        finally { RemoveTree(e.Root); }
    }

    // ---- journal store ---------------------------------------------------------------------

    [Test]
    void Journal_SaveLoadDelete_RoundTrips()
    {
        var cfg = Directory.CreateTempSubdirectory("grog-jrnl-").FullName;
        try
        {
            var store = new ReorgJournalStore(cfg);
            var job = new ReorgJob { Reason = ReorgReason.Layout };
            job.Moves.Add(new ReorgMove { GogId = 9, FileKey = "k", FromRel = "x", ToRel = "Games/x", Kind = ReorgMoveKind.Rename });

            store.Save(job);
            Assert.True(store.Exists, "written");

            var loaded = store.Load();
            Assert.NotNull(loaded);
            Assert.Equal(job.Id, loaded!.Id, "same id");
            Assert.Equal(1, loaded.Moves.Count, "moves round-trip");
            Assert.Equal(ReorgMoveKind.Rename, loaded.Moves[0].Kind, "enum round-trips");

            store.Delete();
            Assert.False(store.Exists, "deleted");
        }
        finally { Directory.Delete(cfg, true); }
    }

    // ---- planner ---------------------------------------------------------------------------

    // ---- change-location (root path repoint) -----------------------------------------------

    [Test]
    void ForRootPathChange_KeepsRelKeepsRoot_OnlyFolderMoves()
    {
        var m = new LibraryManifest { PrimaryRootId = "primary" };
        var it = new LibraryItem { GogId = 1, Title = "Alpha", Slug = "alpha" };
        it.Files.Add(new GameFile { FileKey = "alpha/setup.exe", RootId = "primary", LocalRelativePath = "Games/alpha/setup.exe", LocalSizeBytes = 100 });
        it.Files.Add(new GameFile { FileKey = "alpha/manual.pdf", RootId = null, LocalRelativePath = "Games/alpha/Extras/manual.pdf", LocalSizeBytes = 20 });
        it.Files.Add(new GameFile { FileKey = "beta/setup.exe", RootId = "secondary", LocalRelativePath = "Games/beta/setup.exe", LocalSizeBytes = 50 });
        m.Items.Add(it);

        var job = ReorgPlanner.ForRootPathChange(m, "primary", @"C:\old", @"C:\new");

        Assert.Equal(ReorgReason.ChangeFolder, job.Reason, "change-location reason");
        Assert.Equal(2, job.Total, "two files on 'primary' (null-root counts as primary); not the 'secondary' one");

        var setup = job.Moves.First(x => x.FileKey == "alpha/setup.exe");
        Assert.Equal("Games/alpha/setup.exe", setup.ToRel, "relative path unchanged");
        Assert.Null(setup.ToRootId, "stays on same root");
        Assert.Equal(Path.Combine(@"C:\old", "Games", "alpha", "setup.exe"), setup.FromAbs, "src under old path");
        Assert.Equal(Path.Combine(@"C:\new", "Games", "alpha", "setup.exe"), setup.ToAbs, "dst under new path");
        Assert.Equal(ReorgMoveKind.Rename, setup.Kind, "same volume (C:) -> rename");
    }

    [Test]
    void ForRootPathChange_NoFilesOnRoot_EmptyJob()
    {
        var m = new LibraryManifest { PrimaryRootId = "primary" };
        var it = new LibraryItem { GogId = 1, Title = "Alpha", Slug = "alpha" };
        it.Files.Add(new GameFile { FileKey = "a/x", RootId = "secondary", LocalRelativePath = "Games/a/x", LocalSizeBytes = 1 });
        m.Items.Add(it);

        var job = ReorgPlanner.ForRootPathChange(m, "primary", @"C:\old", @"D:\new");
        Assert.Equal(0, job.Total, "no files on 'primary'");
    }

    // ---- reorder pending moves -------------------------------------------------------------

    static ReorgJob JobWith(params (string key, ReorgMoveState st)[] moves)
    {
        var job = new ReorgJob { Reason = ReorgReason.ChangeFolder };
        foreach (var (key, st) in moves)
            job.Moves.Add(new ReorgMove { FileKey = key, Title = key, State = st, Kind = ReorgMoveKind.Rename });
        return job;
    }

    [Test]
    void MovePendingTo_ReordersOnlyPending()
    {
        var job = JobWith(("a", ReorgMoveState.Pending), ("b", ReorgMoveState.Pending), ("c", ReorgMoveState.Pending));
        var c = job.Moves.First(x => x.FileKey == "c");

        ReorgQueueOps.MovePendingTo(job, c, 0);   // send 'c' to the front of the queue

        Assert.Equal("c", job.Pending()[0].FileKey, "c is now first to run");
        Assert.Equal("a", job.Pending()[1].FileKey, "a shifts back");
        Assert.Equal("b", job.Pending()[2].FileKey, "b shifts back");
    }

    [Test]
    void SortPending_OrdersOnlyPendingAndReportsChange()
    {
        var job = JobWith(("done", ReorgMoveState.Done), ("act", ReorgMoveState.Copied),
                          ("b", ReorgMoveState.Pending), ("c", ReorgMoveState.Pending), ("a", ReorgMoveState.Pending));
        Assert.True(ReorgQueueOps.SortPending(job, m => m.FileKey, ascending: true), "order changed");
        Assert.Equal("done,act,a,b,c", string.Join(",", job.Moves.Select(m => m.FileKey)), "settled and in-flight keep their slots");
        Assert.False(ReorgQueueOps.SortPending(job, m => m.FileKey, ascending: true), "already in that order: no-op");
        Assert.True(ReorgQueueOps.SortPending(job, m => m.FileKey, ascending: false));
        Assert.Equal("done,act,c,b,a", string.Join(",", job.Moves.Select(m => m.FileKey)));
    }

    [Test]
    void MovePendingTo_KeepsSettledAndActiveSlots()
    {
        // 'done' already ran, 'act' is in flight (Copied); only the two Pending ones may reorder.
        var job = JobWith(("done", ReorgMoveState.Done), ("act", ReorgMoveState.Copied),
                          ("p1", ReorgMoveState.Pending), ("p2", ReorgMoveState.Pending));
        var p2 = job.Moves.First(x => x.FileKey == "p2");

        ReorgQueueOps.MovePendingTo(job, p2, 0);

        Assert.Equal("done", job.Moves[0].FileKey, "settled move keeps its slot");
        Assert.Equal("act", job.Moves[1].FileKey, "in-flight move keeps its slot");
        Assert.Equal("p2", job.Moves[2].FileKey, "p2 jumped ahead of p1");
        Assert.Equal("p1", job.Moves[3].FileKey, "p1 shifted back");
    }

    [Test]
    void MovePendingTo_NonPendingItem_IsNoOp()
    {
        var job = JobWith(("act", ReorgMoveState.Copied), ("p1", ReorgMoveState.Pending));
        var act = job.Moves.First(x => x.FileKey == "act");
        ReorgQueueOps.MovePendingTo(job, act, 1);   // can't reprioritize the in-flight move
        Assert.Equal("act", job.Moves[0].FileKey, "order unchanged");
        Assert.Equal("p1", job.Moves[1].FileKey, "order unchanged");
    }

    [Test]
    void Planner_SkipsFilesAlreadyInPlace()
    {
        var e = NewSetup();
        try
        {
            // One correct, one flat.
            AddFileOnDisk(e, 1, "ok", "Ok", FileKind.Installer, "Games/ok/setup.exe", "x");
            AddFileOnDisk(e, 2, "old", "Old", FileKind.Installer, "old/setup.exe", "y");

            var job = ReorgPlanner.ForLayoutChange(e.Store.Current, e.Layout);
            Assert.Equal(1, job.Total, "only the misplaced file is planned");
            Assert.Equal("old/setup.exe", job.Moves[0].FromRel, "the flat one");
        }
        finally { RemoveTree(e.Root); }
    }

    // 1283 (QA 09-30 B1): the reachability probe gets the storage folder, never Path.GetPathRoot (always "/" on
    // macOS/Linux, so a pulled source read reachable and its moves were Skipped and repathed).
    [Test]
    public void StorageFolderOf_strips_the_relative_part()
    {
        var sep = Path.DirectorySeparatorChar;
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "grog-sf-root"));
        var abs = Path.Combine(root, "Games", "mover", "setup.exe");
        Assert.Equal(root, ReorgRunner.StorageFolderOf(abs, $"Games{sep}mover{sep}setup.exe"));
        Assert.Equal(root, ReorgRunner.StorageFolderOf(abs, "Games/mover/setup.exe"), "forward slashes in the journal");
        Assert.Equal(Path.GetDirectoryName(abs), ReorgRunner.StorageFolderOf(abs, "other/path.exe"), "mismatch: the file's own folder");
        Assert.False(new ReorgRunner(null!, null!).VolumeReachable(Path.Combine(Path.GetPathRoot(root)!, "no-such-mount", "stick")) && !OperatingSystem.IsWindows(), "nothing under the POSIX root alone counts as reachable");
    }

    // 1283 (review 09-30): when the move names its storage folder (rel lines up with abs), that folder itself must
    // exist, whatever ancestor does: an unplugged stick's folder is gone even when its parent is not.
    [Test]
    void A_missing_storage_folder_is_unreachable_even_under_an_existing_parent()
    {
        var parent = Path.Combine(Path.GetTempPath(), "grog-reach-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(parent);
        try
        {
            var runner = new ReorgRunner(null!, null!) { VolumeReachable = _ => true };   // the drive probe says yes; the folder decides
            var gone = Path.Combine(parent, "stick");
            var mv = new ReorgMove { FromAbs = Path.Combine(gone, "Games", "t", "x.exe"), FromRel = "Games/t/x.exe", ToAbs = Path.Combine(parent, "Games", "t", "x.exe"), ToRel = "Games/t/x.exe" };
            Assert.False(runner.SourceReachableForTest(mv), "storage folder missing: unreachable");
            Assert.True(runner.TargetReachableForTest(mv), "the parent folder exists: reachable");
            Directory.CreateDirectory(gone);
            Assert.True(runner.SourceReachableForTest(mv), "folder back: reachable");
        }
        finally { try { Directory.Delete(parent, true); } catch { } }
    }
}
