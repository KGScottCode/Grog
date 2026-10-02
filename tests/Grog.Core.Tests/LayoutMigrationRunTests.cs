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
using Grog.Core.Volumes;

/// <summary>The shared layout-migration orchestrator (S2.1): the App's CheckLayoutMigration / MigrateNow rules
/// and the CLI's `reorg layout`, over a temp root + JsonManifestStore, no fakes needed (the runner is disk-only).</summary>
[NewBatch]
[Trait("reorg")]
public class LayoutMigrationRunTests
{
    sealed record Env(JsonManifestStore Store, string Root, BackupLayout Layout, string ConfigDir);

    static Env NewSetup()
    {
        var root = Directory.CreateTempSubdirectory("grog-layoutrun-").FullName;
        var cfg = Path.Combine(root, "_cfg");
        Environment.SetEnvironmentVariable("GROG_CONFIG_DIR", cfg);
        var paths = GrogPaths.Resolve(root);
        var store = new JsonManifestStore(paths);
        store.LoadAsync().GetAwaiter().GetResult();
        var layout = new BackupLayout(store.Current, root);   // registers the primary root
        store.Current.SetExtrasLayout(ExtrasPlacement.SeparateByGame);
        return new Env(store, root, layout, paths.ConfigDir);
    }

    /// <summary>A flat-layout file (&lt;root&gt;/&lt;slug&gt;/&lt;name&gt;), optionally absent from disk.</summary>
    static GameFile AddFile(Env e, long gogId, string slug, string title, string rel, string content, bool onDisk = true)
    {
        var item = e.Store.Current.Items.FirstOrDefault(i => i.GogId == gogId);
        if (item is null)
        {
            item = new LibraryItem { GogId = gogId, Title = title, Slug = slug };
            e.Store.Current.Items.Add(item);
        }
        if (onDisk)
        {
            var abs = Path.Combine(e.Root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
            File.WriteAllText(abs, content);
        }
        var f = new GameFile
        {
            GameGogId = gogId, FileKey = $"{slug}/{Path.GetFileName(rel)}", Kind = FileKind.Installer, Name = Path.GetFileName(rel),
            RootId = e.Store.Current.PrimaryRootId, LocalRelativePath = rel,
            State = onDisk ? FileState.Present : FileState.Missing, ExpectedSizeBytes = content.Length,
            LocalSizeBytes = onDisk ? content.Length : null,
        };
        item.Files.Add(f);
        return f;
    }

    sealed class LogHost : NullBackupHost
    {
        public List<string> Lines { get; } = new();
        public override void Log(string message, bool isError = false) => Lines.Add(message);
    }

    [Test]
    async Task Plan_OffersOnlyFilesOnDisk_ButTheJobKeepsTheRest()
    {
        var e = NewSetup();
        try
        {
            AddFile(e, 1, "alpha", "Alpha", "alpha/setup.exe", "aaaa");
            AddFile(e, 1, "alpha", "Alpha", "alpha/patch.bin", "bb", onDisk: false);   // record only: bytes gone
            AddFile(e, 2, "beta", "Beta", "beta/setup.exe", "cccccc");
            AddFile(e, 3, "gamma", "Gamma", "Games/gamma/setup.exe", "already-there");   // already in the new layout

            var plan = await LayoutMigrationRun.PlanAsync(e.Store, e.Root, CancellationToken.None);
            Assert.NotNull(plan, "two flat files on disk -> an offer");
            Assert.Equal(LayoutPlanKind.FlatLayout, plan!.Kind);
            Assert.Equal(2, plan.Count, "the prompt promises only what exists on disk (owner-hit 08-30)");
            Assert.Equal(2, plan.Games);
            Assert.Equal(10, plan.Bytes, "bytes of the shown moves only");
            Assert.True(plan.Moves.All(m => File.Exists(m.FromAbs)), "every offered move has a source file");
            Assert.Equal(3, plan.Job.Moves.Count, "the executable job still carries the missing record (runner skips + repaths it)");
            Assert.False(plan.HasUnavailableDrives);
        }
        finally { Directory.Delete(e.Root, true); }
    }

    [Test]
    async Task Plan_IsNull_WhenNothingIsOutOfPlace()
    {
        var e = NewSetup();
        try
        {
            AddFile(e, 3, "gamma", "Gamma", "Games/gamma/setup.exe", "fine");
            AddFile(e, 4, "delta", "Delta", "delta/setup.exe", "gone", onDisk: false);   // misplaced record, no bytes
            var plan = await LayoutMigrationRun.PlanAsync(e.Store, e.Root, CancellationToken.None);
            Assert.Null(plan, "nothing on disk to move -> no offer, even though a manifest path is stale");
        }
        finally { Directory.Delete(e.Root, true); }
    }

    [Test]
    async Task Plan_RefusesWhileAReorgJournalIsUnfinished()
    {
        var e = NewSetup();
        try
        {
            AddFile(e, 1, "alpha", "Alpha", "alpha/setup.exe", "aaaa");
            // An interrupted reorganize OWNS the misplaced files: the resume prompt handles them (owner-hit 08-30).
            var pending = new ReorgJob { Reason = ReorgReason.Layout };
            pending.Moves.Add(new ReorgMove { GogId = 1, FileKey = "alpha/setup.exe", FromRel = "alpha/setup.exe", ToRel = "Games/alpha/setup.exe", State = ReorgMoveState.Pending });
            Directory.CreateDirectory(e.ConfigDir);
            new ReorgJournalStore(e.ConfigDir).Save(pending);

            Assert.Null(await LayoutMigrationRun.PlanAsync(e.Store, e.Root, CancellationToken.None), "unfinished journal -> no second offer");

            // A COMPLETE journal left behind does not block.
            pending.Moves[0].State = ReorgMoveState.Done;
            new ReorgJournalStore(e.ConfigDir).Save(pending);
            Assert.NotNull(await LayoutMigrationRun.PlanAsync(e.Store, e.Root, CancellationToken.None), "a finished journal is not an owner");
        }
        finally { Directory.Delete(e.Root, true); }
    }

    [Test]
    async Task ForLayoutChange_PlansFromTheManifest_AsLayoutChange()
    {
        var e = NewSetup();
        try
        {
            AddFile(e, 1, "alpha", "Alpha", "alpha/setup.exe", "aaaa");
            AddFile(e, 2, "beta", "Beta", "beta/setup.exe", "bb", onDisk: false);
            var plan = await LayoutMigrationRun.ForLayoutChangeAsync(e.Store, e.Root, CancellationToken.None);
            Assert.NotNull(plan);
            Assert.Equal(LayoutPlanKind.LayoutChange, plan!.Kind);
            Assert.Equal(2, plan.Count, "the layout-change offer is manifest-wide (the App's OfferLayoutReorg)");
            Assert.Equal(ReorgReason.Layout, plan.Job.Reason);

            AddFile(e, 3, "gamma", "Gamma", "Games/gamma/setup.exe", "ok");
            e.Store.Current.Items.RemoveAll(i => i.GogId is 1 or 2);
            Assert.Null(await LayoutMigrationRun.ForLayoutChangeAsync(e.Store, e.Root, CancellationToken.None), "nothing out of place -> null");
        }
        finally { Directory.Delete(e.Root, true); }
    }

    [Test]
    async Task Run_MovesTwoFiles_RepathsManifest_CompletesJournal()
    {
        var e = NewSetup();
        try
        {
            var fa = AddFile(e, 1, "alpha", "Alpha", "alpha/setup.exe", "alpha-bytes");
            var fb = AddFile(e, 2, "beta", "Beta", "beta/setup.exe", "beta-bytes");
            var plan = await LayoutMigrationRun.PlanAsync(e.Store, e.Root, CancellationToken.None);
            Assert.NotNull(plan);

            var host = new LogHost();
            var progress = new List<ReorgProgress>();
            var r = await LayoutMigrationRun.RunAsync(e.Store, e.Root, plan!, host, progress.Add, CancellationToken.None);

            Assert.Equal(ReorgOutcome.Completed, r.Outcome);
            Assert.True(r.Completed);
            Assert.Equal(2, r.Moved);
            Assert.Equal(0, r.Skipped); Assert.Equal(0, r.Failed); Assert.False(r.Canceled); Assert.Null(r.Error);

            // Files landed in the new layout, sources gone (same-drive rename), old folders pruned.
            Assert.Equal("alpha-bytes", File.ReadAllText(Path.Combine(e.Root, "Games", "alpha", "setup.exe")));
            Assert.Equal("beta-bytes", File.ReadAllText(Path.Combine(e.Root, "Games", "beta", "setup.exe")));
            Assert.False(File.Exists(Path.Combine(e.Root, "alpha", "setup.exe")), "source renamed away");
            Assert.False(Directory.Exists(Path.Combine(e.Root, "beta")), "emptied flat folder pruned");

            // Manifest repathed and persisted.
            Assert.Equal("Games/alpha/setup.exe", fa.LocalRelativePath);
            Assert.Equal("Games/beta/setup.exe", fb.LocalRelativePath);
            var reloaded = new JsonManifestStore(GrogPaths.Resolve(e.Root));
            await reloaded.LoadAsync();
            Assert.Equal("Games/beta/setup.exe", reloaded.Current.ItemById(2)!.Files[0].LocalRelativePath, "saved to disk");

            // Journal completed (deleted) -> a second plan finds nothing.
            Assert.False(new ReorgJournalStore(e.ConfigDir).Exists, "a clean finish removes the journal");
            Assert.Null(await LayoutMigrationRun.PlanAsync(e.Store, e.Root, CancellationToken.None), "nothing left to offer");
            Assert.True(progress.Count >= 2, "progress fired per file");
            Assert.Equal(2, progress[^1].FilesDone);
        }
        finally { Directory.Delete(e.Root, true); }
    }

    [Test]
    async Task Run_RefusesAPlanWithAnOfflineDrive()
    {
        var e = NewSetup();
        try
        {
            var job = new ReorgJob { Reason = ReorgReason.Layout };
            job.NoteUnavailableDrive("USB Backup");
            var plan = new LayoutMigrationPlan(LayoutPlanKind.LayoutChange, job, Array.Empty<LayoutMigrationService.Move>(), 0, 0);
            var host = new LogHost();
            var r = await LayoutMigrationRun.RunAsync(e.Store, e.Root, plan, host, null, CancellationToken.None);
            Assert.Equal(0, r.Moved);
            Assert.NotNull(r.PauseReason);
            Assert.Contains("USB Backup", r.PauseReason!);
            Assert.Single(host.Lines, "the host was told which drive to reconnect");
            Assert.False(new ReorgJournalStore(e.ConfigDir).Exists, "nothing journaled");
        }
        finally { Directory.Delete(e.Root, true); }
    }
}
