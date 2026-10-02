// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grog.Core.Download;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Runs;
using Grog.Core.Tests.Framework;
using Grog.Core.Volumes;

namespace Grog.Core.Tests;

/// <summary>(Re-plan on sort 09-10) Placement is ORDER-DEPENDENT: the planner walks the queue in order and
/// consumes each device's free space greedily, so a different order produces a different set of won't-fit
/// files. Re-sorting the queue therefore invalidates the fit decision.
///
/// The owner hit this with a size-ascending sort: Resident Evil HD REMASTER (Part 1 of 6) is a genuine 1 MB
/// file, so it sorted to the very head of the queue, took the pill, and sat at "0 B / 1.00 MB / 0%" forever
/// while 13 MB/s flowed behind it. Its GAME did not fit (games are placed whole), so every part including
/// the 1 MB one was dropped at <see cref="BackupQueueBuilder.Place"/> and never handed to the engine.
///
/// The trap these pin: <see cref="BackupQueueBuilder.Replan"/> works through <c>engine.Retarget</c>, whose
/// pending list is what the ENGINE HOLDS. A file dropped at Place is invisible to it, so no re-sort and no
/// amount of freed space could ever bring it back -- it was pinned to won't-fit for the life of the run.
/// <see cref="BackupQueueBuilder.ReplanWholeQueue"/> reads the persisted queue instead, so a file can cross
/// back in both directions.</summary>
[NewBatch]
[Trait("placement")]
public sealed class ReplanOnSortTests
{
    private sealed class MemoryStore : IManifestStore
    {
        public MemoryStore(LibraryManifest m) => Current = m;
        public LibraryManifest Current { get; }
        public ManifestGate Gate { get; } = new();
        public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    // One store per manifest, so the gate identity is stable across calls within a test.
    private static readonly Dictionary<LibraryManifest, MemoryStore> _stores = new();
    private static MemoryStore Store(LibraryManifest m)
    {
        lock (_stores)
        {
            if (!_stores.TryGetValue(m, out var s)) _stores[m] = s = new MemoryStore(m);
            return s;
        }
    }

    private static GameFile F(long game, string key, long size, FileKind kind = FileKind.Installer)
        => new() { GameGogId = game, FileKey = key, Name = key, Kind = kind, State = FileState.NotBackedUp,
                   ExpectedSizeBytes = size, Os = kind == FileKind.Installer ? "windows" : "",
                   Language = kind == FileKind.Installer ? "English" : "" };

    /// <summary>One small drive. "Big" (100) cannot fit; "small" (10) can.</summary>
    private static LibraryManifest Lib()
    {
        var m = new LibraryManifest();
        m.Items.Add(new LibraryItem { GogId = 1, Title = "Big", Slug = "big", Type = ProductType.Game,
                                      Files = { F(1, "big-setup", 100) } });
        m.Items.Add(new LibraryItem { GogId = 2, Title = "Small", Slug = "small", Type = ProductType.Game,
                                      Files = { F(2, "small-setup", 10) } });
        m.Roots.Add(new BackupRoot { Id = "p", Label = "Primary" });
        m.PrimaryRootId = "p";
        return m;
    }

    private static DownloadEngine Engine()
        => new(null!, new System.Net.Http.HttpClient()) { BackupRoot = System.IO.Path.GetTempPath() };

    private static string Held(DownloadEngine e)
        => string.Join(",", e.Snapshot.Where(t => t.State is DownloadTaskState.Pending or DownloadTaskState.Active)
                                      .Select(t => t.File.FileKey).OrderBy(k => k));

    /// <summary>THE BUG. A file that did not fit at Place time is not in the engine, so Replan cannot see it.
    /// This is the behaviour that stranded the owner's 1 MB Resident Evil part.</summary>
    [Test]
    public void Replan_cannot_rescue_a_file_that_never_entered_the_engine()
    {
        var m = Lib();
        var engine = Engine();
        m.Downloads.Enqueue(1, "big-setup", "p");   // queued, but held at Place: never enqueued on the engine
        var devices = new[] { new DeviceSpace("p", 1000, true) };   // plenty of room NOW

        Assert.Equal(0, BackupQueueBuilder.Replan(m, devices, engine), "no pending tasks: nothing to retarget");
        Assert.Equal("", Held(engine), "the engine still holds nothing, however much space appeared");
    }

    /// <summary>The fix: the whole-queue pass reads the persisted queue, so the same file crosses back.</summary>
    [Test]
    public void ReplanWholeQueue_admits_a_held_file_once_it_fits()
    {
        var m = Lib();
        var engine = Engine();
        m.Downloads.Enqueue(1, "big-setup", "p");
        var devices = new[] { new DeviceSpace("p", 1000, true) };

        Assert.Equal(1, BackupQueueBuilder.ReplanWholeQueue(Store(m), devices, engine), "one file changed side");
        Assert.Equal("big-setup", Held(engine), "it is in the engine and will actually download");
    }

    /// <summary>(QA 09-18) A task the engine cancelled while it was ACTIVE stays in its list as Canceled. That
    /// entry is not a membership: once the file fits again the whole-queue pass must hand it back to the engine,
    /// or the flag says Fits while nothing downloads it.</summary>
    [Test]
    public void ReplanWholeQueue_readmits_a_file_whose_task_was_cancelled_mid_transfer()
    {
        var m = Lib();
        var engine = Engine();
        m.Downloads.Enqueue(1, "big-setup", "p");
        var stale = new DownloadTask { File = m.Items[0].Files[0], GameTitle = "Big", TargetRootId = "p", State = DownloadTaskState.Canceled };
        engine.EnqueueRange(new[] { stale });
        stale.State = DownloadTaskState.Canceled;   // as the engine's list looks after a cancel landed on an active worker
        Assert.Equal("", Held(engine), "a Canceled task is not held");
        var devices = new[] { new DeviceSpace("p", 1000, true) };

        Assert.Equal(1, BackupQueueBuilder.ReplanWholeQueue(Store(m), devices, engine), "it fits: one change");
        Assert.Equal("big-setup", Held(engine), "and it is back in the engine as a live task");
    }

    /// <summary>(09-13) No engine: the records are still rewritten, so an idle sort or an idle Storage tick
    /// leaves the red rows, the banner and "Remove them" reading one fact.</summary>
    [Test]
    public void ReplanWholeQueue_without_an_engine_still_rewrites_the_flags()
    {
        var m = Lib();
        m.Downloads.Enqueue(1, "big-setup", "p"); m.Downloads.Enqueue(2, "small-setup", "p");
        BackupQueueBuilder.ReplanWholeQueue(Store(m), new[] { new DeviceSpace("p", 50, true) }, engine: null);
        var snap = m.Downloads.Snapshot();
        Assert.False(snap.First(q => q.FileKey == "big-setup").Fits, "big is flagged");
        Assert.True(snap.First(q => q.FileKey == "small-setup").Fits, "small fits");
        Assert.Equal("p", snap.First(q => q.FileKey == "small-setup").TargetRootId, "and carries its target");
    }

    /// <summary>(09-16) The plan reports what is STILL FREE per device once every fitting file has its room: the
    /// divider's "still free" figure and the room the row menus test against. Priced like the planner: a
    /// resumable partial is already out of the drive's free figure, so it is not charged again.</summary>
    [Test]
    public void ReplanWholeQueueDetailed_reports_the_room_left_after_the_fitting_files()
    {
        var m = Lib();
        m.Items[1].Files[0].HasPartial = true; m.Items[1].Files[0].PartialBytes = 4;   // small: 10 total, 4 on disk
        m.Downloads.Enqueue(1, "big-setup", "p"); m.Downloads.Enqueue(2, "small-setup", "p");
        var r = BackupQueueBuilder.ReplanWholeQueueDetailed(Store(m), new[] { new DeviceSpace("p", 50, true) }, engine: null);
        Assert.False(m.Downloads.Snapshot().First(q => q.FileKey == "big-setup").Fits, "big is flagged");
        Assert.Equal(44L, r.FreeAfter.First(d => d.RootId == "p").FreeBytes, "50 free - (10 - 4 already on disk); the flagged file reserves nothing");
    }

    /// <summary>(09-16) With no engine, "changed" is the flags that flipped: a second idle pass over the same
    /// order reports 0, not one per fitting file (the log said "14 file(s) changed" on every drag).</summary>
    [Test]
    public void ReplanWholeQueue_without_an_engine_counts_only_flag_flips()
    {
        var m = Lib();
        m.Downloads.Enqueue(1, "big-setup", "p"); m.Downloads.Enqueue(2, "small-setup", "p");
        var devices = new[] { new DeviceSpace("p", 50, true) };
        Assert.Equal(1, BackupQueueBuilder.ReplanWholeQueue(Store(m), devices, engine: null), "big flips to won't-fit");
        Assert.Equal(0, BackupQueueBuilder.ReplanWholeQueue(Store(m), devices, engine: null), "same order: nothing changed");
    }

    /// <summary>(09-16) A fit is a per-device test, so the leftover is reported per device, never summed.</summary>
    [Test]
    public void ReplanWholeQueueDetailed_keeps_room_per_device()
    {
        var m = Lib();
        m.Roots.Add(new BackupRoot { Id = "s", Label = "Secondary" });
        m.Downloads.Enqueue(1, "big-setup", "p"); m.Downloads.Enqueue(2, "small-setup", "p");
        var r = BackupQueueBuilder.ReplanWholeQueueDetailed(Store(m),
            new[] { new DeviceSpace("p", 60, true), new DeviceSpace("s", 60, true) }, engine: null);
        // big (100) fits on neither drive alone though 120 is free in total; small (10) lands on p.
        Assert.False(m.Downloads.Snapshot().First(q => q.FileKey == "big-setup").Fits, "big fits on no single drive");
        Assert.Equal(50L, r.FreeAfter.First(d => d.RootId == "p").FreeBytes, "p after small");
        Assert.Equal(60L, r.FreeAfter.First(d => d.RootId == "s").FreeBytes, "s untouched");
    }

    /// <summary>(09-13) Replan (the in-run pass) hands back the tasks that stopped fitting so the caller can cancel
    /// them off the gate. Before, the record said won't-fit while the engine kept the task Pending, and "Remove
    /// them" removed the record but the file downloaded anyway.</summary>
    [Test]
    public void Replan_reports_the_tasks_that_stopped_fitting()
    {
        var m = Lib();
        var engine = Engine();
        m.Downloads.Enqueue(1, "big-setup", "p");
        BackupQueueBuilder.ReplanWholeQueue(Store(m), new[] { new DeviceSpace("p", 1000, true) }, engine);
        Assert.Equal("big-setup", Held(engine));

        BackupQueueBuilder.Replan(m, new[] { new DeviceSpace("p", 5, true) }, engine, out var dropped);
        Assert.Equal(1, dropped.Count, "the one that no longer fits");
        Assert.Equal("big-setup", dropped[0].File.FileKey);
        Assert.False(m.Downloads.Snapshot()[0].Fits, "record flagged");
        engine.CancelRange(dropped);
        Assert.Equal("", Held(engine), "and the caller's cancel takes it out of the engine");
    }

    /// <summary>(09-13) A file that left the queue between the snapshot and the record write (settled, or the
    /// row's x) stays gone. Enqueue would have appended it back as a phantom row the engine never holds.</summary>
    [Test]
    public void A_replan_never_resurrects_an_entry_that_left_the_queue()
    {
        var m = Lib();
        var engine = Engine();
        m.Downloads.Enqueue(1, "big-setup", "p"); m.Downloads.Enqueue(2, "small-setup", "p");
        BackupQueueBuilder.ReplanWholeQueue(Store(m), new[] { new DeviceSpace("p", 1000, true) }, engine);
        Assert.Equal("big-setup,small-setup", Held(engine));

        m.Downloads.Remove(2, "small-setup");   // the user's x, while the engine still holds the task
        BackupQueueBuilder.Replan(m, new[] { new DeviceSpace("p", 1000, true) }, engine, out _);
        Assert.Equal(1, m.Downloads.Snapshot().Count, "in-run replan: not re-added");
        BackupQueueBuilder.ReplanWholeQueue(Store(m), new[] { new DeviceSpace("p", 1000, true) }, engine);
        Assert.Equal(1, m.Downloads.Snapshot().Count, "whole-queue replan: not re-added");
    }

    /// <summary>Order decides who fits. Small first takes the room, so Big is the one left out; Big first
    /// takes it instead. This is exactly why a re-sort has to re-run the calc.</summary>
    [Test]
    public void The_queue_order_decides_which_file_is_left_out()
    {
        var devices = new[] { new DeviceSpace("p", 50, true) };   // room for Small (10) but not Big (100)

        var m1 = Lib(); var e1 = Engine();
        m1.Downloads.Enqueue(2, "small-setup", "p"); m1.Downloads.Enqueue(1, "big-setup", "p");
        BackupQueueBuilder.ReplanWholeQueue(Store(m1), devices, e1);
        Assert.Equal("small-setup", Held(e1), "small first: it fits, big does not");

        // Same library, same drive, reversed order: Big still cannot fit 100 into 50, so Small survives.
        var m2 = Lib(); var e2 = Engine();
        m2.Downloads.Enqueue(1, "big-setup", "p"); m2.Downloads.Enqueue(2, "small-setup", "p");
        BackupQueueBuilder.ReplanWholeQueue(Store(m2), devices, e2);
        Assert.Equal("small-setup", Held(e2), "big never fits at 100 into 50");
    }

    /// <summary>The other direction: a file that stops fitting leaves the engine, but its QUEUE ENTRY STAYS.
    /// It is still queued and still drawn, it simply has nowhere to land. (Canceled tasks are left alone by
    /// DownloadSettlement for exactly this reason.) Losing the entry would silently drop the file from the run.</summary>
    [Test]
    public void A_file_that_stops_fitting_leaves_the_engine_but_stays_queued()
    {
        var m = Lib();
        var engine = Engine();
        m.Downloads.Enqueue(1, "big-setup", "p");
        BackupQueueBuilder.ReplanWholeQueue(Store(m), new[] { new DeviceSpace("p", 1000, true) }, engine);
        Assert.Equal("big-setup", Held(engine), "in, while there was room");

        int changed = BackupQueueBuilder.ReplanWholeQueue(Store(m), new[] { new DeviceSpace("p", 5, true) }, engine);
        Assert.Equal(1, changed);
        Assert.Equal("", Held(engine), "out of the engine now that nothing has room");
        Assert.Equal(1, m.Downloads.Snapshot().Count, "still queued: the run has not silently dropped it");
    }

    /// <summary>Idempotent: a second pass over an unchanged queue moves nothing. The App calls this on every
    /// sort click, so a no-op sort must not churn the engine (which would pre-empt in-flight files).</summary>
    [Test]
    public void A_second_pass_over_an_unchanged_queue_changes_nothing()
    {
        var m = Lib();
        var engine = Engine();
        m.Downloads.Enqueue(2, "small-setup", "p");
        var devices = new[] { new DeviceSpace("p", 1000, true) };

        Assert.Equal(1, BackupQueueBuilder.ReplanWholeQueue(Store(m), devices, engine), "first pass admits it");
        Assert.Equal(0, BackupQueueBuilder.ReplanWholeQueue(Store(m), devices, engine), "second pass is a no-op");
        Assert.Equal("small-setup", Held(engine));
    }

    /// <summary>THE DEADLOCK, pinned (owner-hit 09-10: "Installers first" hard-locked the app, End Task).
    /// Engine calls raise TaskChanged SYNCHRONOUSLY, and RunSettler.SettlePass takes the settler lock and THEN
    /// the manifest gate. So an engine call made while holding the gate deadlocks against any worker already
    /// inside SettlePass. This asserts the gate is not held for any event this raises -- the property that
    /// makes that deadlock impossible, rather than the timing that makes it likely.</summary>
    [Test]
    public void The_manifest_gate_is_never_held_while_the_engine_raises_events()
    {
        var m = Lib();
        var store = Store(m);
        var engine = Engine();
        m.Downloads.Enqueue(1, "big-setup", "p");
        m.Downloads.Enqueue(2, "small-setup", "p");

        int events = 0; bool heldDuringAnEvent = false;
        engine.TaskChanged += _ => { events++; if (store.Gate.IsHeld) heldDuringAnEvent = true; };
        engine.QueueChanged += () => { events++; if (store.Gate.IsHeld) heldDuringAnEvent = true; };

        // Admit both, then take the room away so the next pass has to cancel them back out: both directions.
        BackupQueueBuilder.ReplanWholeQueue(store, new[] { new DeviceSpace("p", 1000, true) }, engine);
        BackupQueueBuilder.ReplanWholeQueue(store, new[] { new DeviceSpace("p", 1, true) }, engine);

        Assert.True(events > 0, "the engine did raise events, so the assertion below means something");
        Assert.False(heldDuringAnEvent, "an engine event fired while the manifest gate was held: deadlock shape");
    }

    /// <summary>THE FREEZE, pinned (owner-hit 09-10, TWICE, on "Installers first" -- End Task both times).
    /// Every engine event runs a full RunSettler pass over the whole snapshot AND a full queue-pane rebuild,
    /// so cancelling N files one at a time is O(N * queue): at ~120 files against a ~1100-file queue it is a
    /// multi-minute freeze that is indistinguishable from a hang. The event count per re-plan must be a small
    /// constant, NOT a function of how many files changed side.</summary>
    [Test]
    public void A_replan_that_drops_many_files_raises_a_constant_number_of_events()
    {
        var m = new LibraryManifest();
        for (long g = 1; g <= 60; g++)
        {
            m.Items.Add(new LibraryItem { GogId = g, Title = "g" + g, Slug = "g" + g, Type = ProductType.Game,
                                          Files = { F(g, "f" + g, 10) } });
            m.Downloads.Enqueue(g, "f" + g, "p");
        }
        m.Roots.Add(new BackupRoot { Id = "p", Label = "Primary" });
        m.PrimaryRootId = "p";
        var store = Store(m);
        var engine = Engine();

        // Admit all 60, then take the room away so all 60 have to come back out.
        BackupQueueBuilder.ReplanWholeQueue(store, new[] { new DeviceSpace("p", 100_000, true) }, engine);
        Assert.Equal(60, engine.Snapshot.Count(t => t.State == DownloadTaskState.Pending), "all admitted");

        int events = 0;
        engine.TaskChanged += _ => events++;
        engine.QueueChanged += () => events++;
        int changed = BackupQueueBuilder.ReplanWholeQueue(store, new[] { new DeviceSpace("p", 1, true) }, engine);

        Assert.Equal(60, changed, "all 60 stopped fitting");
        Assert.Equal(0, engine.Snapshot.Count(t => t.State == DownloadTaskState.Pending), "and left the engine");
        // One batched TaskChanged + one QueueChanged. The loop this replaced raised 120.
        Assert.True(events <= 4, $"expected a small constant, got {events} events for 60 cancels");
    }

    /// <summary>An empty queue is not an error and must not touch the engine.</summary>
    [Test]
    public void An_empty_queue_is_a_no_op()
        => Assert.Equal(0, BackupQueueBuilder.ReplanWholeQueue(Store(Lib()), new[] { new DeviceSpace("p", 1000, true) }, Engine()));
    // ---- 09-11: the Fits flag on the persisted entry (inform, never choose) ----

    /// <summary>The queue entry itself says whether the file fits, so the Activity pane can paint WON'T FIT
    /// on the row without asking the engine. Re-decided by ReplanWholeQueue, in BOTH directions.</summary>
    [Test]
    public void ReplanWholeQueue_marks_the_entry_Fits_false_then_true_again()
    {
        var m = Lib(); var engine = Engine();
        m.Downloads.Enqueue(1, "big-setup", "p"); m.Downloads.Enqueue(2, "small-setup", "p");

        BackupQueueBuilder.ReplanWholeQueue(Store(m), new[] { new DeviceSpace("p", 50, true) }, engine);
        var q = m.Downloads.Snapshot();
        Assert.False(q.First(x => x.FileKey == "big-setup").Fits, "big is flagged on its entry");
        Assert.True(q.First(x => x.FileKey == "small-setup").Fits, "small is not");

        BackupQueueBuilder.ReplanWholeQueue(Store(m), new[] { new DeviceSpace("p", 1000, true) }, engine);
        Assert.True(m.Downloads.Snapshot().All(x => x.Fits), "room appeared: the flag clears");
    }

    /// <summary>The flagged row does not move. It is first in the queue before and after, and the engine
    /// simply takes the next fitting row behind it.</summary>
    [Test]
    public void A_wont_fit_row_at_the_head_keeps_its_place_and_the_engine_takes_the_next()
    {
        var m = Lib(); var engine = Engine();
        m.Downloads.Enqueue(1, "big-setup", "p"); m.Downloads.Enqueue(2, "small-setup", "p");
        BackupQueueBuilder.ReplanWholeQueue(Store(m), new[] { new DeviceSpace("p", 50, true) }, engine);

        Assert.Equal("big-setup,small-setup", string.Join(",", m.Downloads.Snapshot().Select(x => x.FileKey)), "order untouched");
        Assert.Equal("small-setup", Held(engine), "the engine holds only what will move");
    }

    /// <summary>Place sets the flag too, so a fresh Back up paints the red rows immediately, before any re-plan.</summary>
    [Test]
    public void Place_sets_Fits_on_the_new_entries()
    {
        var m = Lib();
        var sel = m.Items.Select(i => (i, i.Files[0])).ToList();   // big then small
        var (tasks, held, heldBytes, _) = BackupQueueBuilder.Place(m, null!, new[] { new DeviceSpace("p", 50, true) }, sel);
        Assert.Equal(1, held, "one file held"); Assert.Equal(100L, heldBytes, "the big one's bytes");
        Assert.Equal("small-setup", string.Join(",", tasks.Select(t => t.File.FileKey)), "only the fitting file becomes a task");
        var q = m.Downloads.Snapshot();
        Assert.Equal("big-setup,small-setup", string.Join(",", q.Select(x => x.FileKey)), "both queued, in selection order");
        Assert.False(q[0].Fits, "big flagged"); Assert.True(q[1].Fits, "small not");
    }

    /// <summary>"Remove them from the queue" takes ONLY the flagged rows and leaves the rest in their order.</summary>
    [Test]
    public void RemoveWontFit_takes_only_the_flagged_rows_and_keeps_the_order_of_the_rest()
    {
        var q = new DownloadQueue();
        q.Enqueue(2, "s1", "p", fits: true); q.Enqueue(1, "b1", "p", fits: false);
        q.Enqueue(3, "s2", "p", fits: true); q.Enqueue(4, "b2", "p", fits: false);
        var gone = q.RemoveWontFit();
        Assert.Equal("b1,b2", string.Join(",", gone.Select(x => x.FileKey)), "the removed set");
        Assert.Equal("s1,s2", string.Join(",", q.Snapshot().Select(x => x.FileKey)), "survivors, same order");
        Assert.Equal(0, q.RemoveWontFit().Count, "idempotent");
    }
    /// <summary>(walk 09-19) A 6.1 GB file died at FAT32's 4 GiB ceiling and left a 4 GiB .part: "0 B still free", and
    /// two 1-3 MB files behind it could not land. A partial on a volume that can never hold its file is released,
    /// the record follows the disk, and the room it frees is planned in the SAME pass.</summary>
    [Test]
    public void A_partial_its_volume_can_never_finish_is_released_and_the_room_is_replanned()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "grog-deadpart-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, ".grog-tmp"));
        try
        {
            var m = Lib();
            var big = m.Items[0].Files[0];
            big.ExpectedSizeBytes = 6_549_825_126; big.HasPartial = true; big.PartialBytes = 50; big.PartialRootId = "p";
            var part = DownloadEngine.PartialPathFor(root, big);
            System.IO.File.WriteAllBytes(part, new byte[50]);
            m.Downloads.Enqueue(1, "big-setup", "p");
            m.Downloads.Enqueue(2, "small-setup", "p");   // 10 bytes
            var engine = new DownloadEngine(null!, new System.Net.Http.HttpClient()) { BackupRoot = root };
            var fat = new[] { new DeviceSpace("p", 0, true) { MaxFileBytes = 4294967295L, Path = root } };   // full: the dead partial holds it

            BackupQueueBuilder.ReplanWholeQueue(Store(m), fat, engine);

            Assert.False(System.IO.File.Exists(part), "the dead .part is gone from the drive");
            Assert.False(big.HasPartial, "and the record follows the disk");
            Assert.Equal("small-setup", Held(engine), "the 50 bytes it freed were planned at once: the small file is in the engine");
            Assert.False(m.Downloads.Snapshot().First(q => q.FileKey == "big-setup").Fits, "the big file stays queued as won't fit");
        }
        finally { try { System.IO.Directory.Delete(root, true); } catch { } }
    }

    /// <summary>A partial is NOT released while a drive is unplugged (that drive may be where the file can finish, and
    /// the engine carries a stray partial over), nor when the volume can hold the file (it is simply resumable).</summary>
    [Test]
    public void A_partial_is_kept_while_a_drive_is_offline_or_when_it_is_resumable_where_it_sits()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "grog-keeppart-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(root, ".grog-tmp"));
        try
        {
            var m = Lib();
            var big = m.Items[0].Files[0];
            big.ExpectedSizeBytes = 6_549_825_126; big.HasPartial = true; big.PartialBytes = 50; big.PartialRootId = "p";
            var part = DownloadEngine.PartialPathFor(root, big);
            System.IO.File.WriteAllBytes(part, new byte[50]);
            m.Downloads.Enqueue(1, "big-setup", "p");

            var withOffline = new[] { new DeviceSpace("p", 0, true) { MaxFileBytes = 4294967295L, Path = root }, new DeviceSpace("s", 0, false) };
            BackupQueueBuilder.ReplanWholeQueue(Store(m), withOffline, null);
            Assert.True(System.IO.File.Exists(part), "a drive is unplugged: nothing is thrown away");
            Assert.True(big.HasPartial);

            var ntfsFull = new[] { new DeviceSpace("p", 0, true) { Path = root } };   // no ceiling: just out of room
            BackupQueueBuilder.ReplanWholeQueue(Store(m), ntfsFull, null);
            Assert.True(System.IO.File.Exists(part), "won't fit for lack of room is not dead: it resumes when room appears");
            Assert.True(big.HasPartial);
        }
        finally { try { System.IO.Directory.Delete(root, true); } catch { } }
    }

    /// <summary>(QA 09-19) Two whole-queue passes can each decide to hand over the same newly fitting file (each
    /// snapshots what the engine knows before either enqueues). The engine keeps one live task per file.</summary>
    [Test]
    public void The_engine_keeps_one_live_task_per_file()
    {
        var m = Lib();
        var engine = Engine();
        var f = m.Items[1].Files[0];
        engine.EnqueueRange(new[] { new DownloadTask { File = f, GameTitle = "Small" } });
        engine.EnqueueRange(new[] { new DownloadTask { File = f, GameTitle = "Small" }, new DownloadTask { File = f, GameTitle = "Small" } });
        Assert.Equal(1, engine.Snapshot.Count(t => t.State == DownloadTaskState.Pending));
    }

    /// <summary>(QA 09-19) A queue entry whose game left the library is pruned by the drain instead of riding along
    /// with a stale flag forever.</summary>
    [Test]
    public void A_phantom_queue_entry_leaves_on_the_next_drain()
    {
        var m = Lib();
        m.Downloads.Enqueue(99, "ghost", "p");
        m.Downloads.Enqueue(2, "small-setup", "p");
        var picked = BackupQueueBuilder.Build(m, null, new BackupRunOptions { FromPersistedQueue = true });
        Assert.Equal(1, picked.Count);
        Assert.Equal("small-setup", string.Join(",", m.Downloads.Snapshot().Select(q => q.FileKey)));
    }

    // 1283 (QA 09-30 A1): the whole-queue walk applies Build's rule: a file whose home drive (old build) is unplugged
    // is not handed to the engine on another root. It stays queued, flag untouched, waiting for its drive.
    [Test]
    public void Whole_queue_replan_leaves_a_file_whose_home_drive_is_offline_out_of_the_engine()
    {
        var m = Lib();
        m.Roots.Add(new BackupRoot { Id = "offline", Label = "Away" });
        var small = m.ItemById(2)!.Files[0];
        small.RootId = "offline"; small.State = FileState.Outdated;
        m.Downloads.Enqueue(2, "small-setup");
        var engine = Engine();
        var devices = new[] { new DeviceSpace("p", 1_000, true), new DeviceSpace("offline", 0, false) };
        BackupQueueBuilder.ReplanWholeQueue(Store(m), devices, engine);
        Assert.Equal("", Held(engine), "not fetched onto the primary while its drive is away");
        Assert.True(m.Downloads.Contains(2, "small-setup"), "still queued");
    }

    // 1283 (QA 09-30 A9): an unchecked transfer whose recorded partial already sits on the target volume is charged
    // only what is still to come; those bytes are already out of the drive's free figure.
    [Test]
    public void In_flight_reservation_does_not_double_charge_a_partial_already_on_the_volume()
    {
        var f = F(1, "big-setup", 1_000_000_000);
        f.HasPartial = true; f.PartialBytes = 600_000_000; f.PartialRootId = "p";
        var t = new DownloadTask { File = f, GameTitle = "Big", GameSlug = "big", TargetRootId = "p", BytesReceived = 600_000_000, RoomChecked = false };
        var devices = new[] { new DeviceSpace("p", 10_000_000_000, true) };
        Assert.Equal(10_000_000_000 - 400_000_000, BackupQueueBuilder.ReserveInFlight(devices, new[] { t }, "p")[0].FreeBytes, "same volume: the remainder");
        f.PartialRootId = "elsewhere";
        Assert.Equal(10_000_000_000 - 1_000_000_000, BackupQueueBuilder.ReserveInFlight(devices, new[] { t }, "p")[0].FreeBytes, "partial elsewhere: the whole file is still to come");
    }

    // 1283 (QA 09-30 B6): a file whose .part is being carried between drives is not taken by a worker until released.
    [Test]
    public void A_held_file_is_skipped_by_the_workers_until_released()
    {
        var engine = Engine();
        var a = F(1, "big-setup", 100); var b = F(2, "small-setup", 10);
        engine.EnqueueRange(new[] {
            new DownloadTask { File = a, GameTitle = "Big", GameSlug = "big", TargetRootId = "p" },
            new DownloadTask { File = b, GameTitle = "Small", GameSlug = "small", TargetRootId = "p" } });
        engine.HoldFiles(new[] { (1L, "big-setup") });
        Assert.Equal("small-setup", engine.TakeNextForTest()!.File.FileKey, "the held file is passed over");
        Assert.Null(engine.TakeNextForTest(out bool gated), "nothing else startable while held");
        Assert.True(gated, "and the worker waits for it rather than retiring (review 09-30)");
        engine.ReleaseFiles(new[] { (1L, "big-setup") });
        Assert.Equal("big-setup", engine.TakeNextForTest()!.File.FileKey, "taken once released");
    }
}
