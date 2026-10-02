// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
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

/// <summary>(Re-plan on sort 09-10) The re-plan added on the sort path runs on the UI thread while download
/// workers are settling on their own threads. Both failures it shipped with were of exactly that shape and
/// neither was reachable by a single-threaded test: a lock-order deadlock against RunSettler, then a
/// duplicate enqueue in the window where a task is Completed but its queue entry is not yet removed.
///
/// These drive a REAL RunSettler against a REAL engine while the re-plan runs, and assert that a file is
/// never in the engine twice and that nothing still owed is lost.
///
/// NOT covered here: the deadlock. A two-thread test for it was written and then DELETED, because
/// reintroducing the bug (holding the gate across the engine calls) did not fail it -- it never reproduced
/// the contention it claimed to test. A test that passes with the bug present is worse than no test, because
/// it certifies the untested path as covered. The deadlock is guarded instead by the deterministic property
/// test in ReplanOnSortTests: the manifest gate is never held while the engine raises an event. That one DOES
/// fail when the bug is reintroduced (verified 09-10).</summary>
[NewBatch]
[Trait("placement")]
public sealed class ReplanConcurrencyTests
{
    private sealed class MemoryStore : IManifestStore
    {
        public MemoryStore(LibraryManifest m) => Current = m;
        public LibraryManifest Current { get; }
        public ManifestGate Gate { get; } = new();
        public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private const int N = 120;

    private static (MemoryStore Store, DownloadEngine Engine) Rig()
    {
        var m = new LibraryManifest();
        m.Roots.Add(new BackupRoot { Id = "p", Label = "Primary" });
        m.PrimaryRootId = "p";
        for (long g = 1; g <= N; g++)
        {
            m.Items.Add(new LibraryItem { GogId = g, Title = "g" + g, Slug = "g" + g, Type = ProductType.Game,
                Files = { new GameFile { GameGogId = g, FileKey = "f" + g, Name = "f" + g,
                                         Kind = FileKind.Installer, State = FileState.NotBackedUp,
                                         ExpectedSizeBytes = 10, Os = "windows", Language = "English" } } });
            m.Downloads.Enqueue(g, "f" + g, "p");
        }
        var e = new DownloadEngine(null!, new System.Net.Http.HttpClient()) { BackupRoot = System.IO.Path.GetTempPath() };
        return (new MemoryStore(m), e);
    }

    private static int DuplicateFileKeys(DownloadEngine e)
        => e.Snapshot.GroupBy(t => (t.File.GameGogId, t.File.FileKey)).Count(g => g.Count() > 1);

    /// <summary>THE DUPLICATE. A task that has completed stays in the engine's queue until the settler removes
    /// its manifest entry. Re-planning inside that window must not enqueue the file again.</summary>
    [Test]
    public void Re_planning_while_tasks_complete_never_enqueues_a_file_twice()
    {
        var (store, engine) = Rig();
        using var settler = new RunSettler(store, engine, null, new NullBackupHost());
        var devices = new[] { new DeviceSpace("p", 100_000, true) };

        BackupQueueBuilder.ReplanWholeQueue(store, devices, engine);
        Assert.Equal(N, engine.Snapshot.Count(), "all admitted to start");

        int worstDuplicates = 0;
        var rnd = new Random(20260910);
        for (int round = 0; round < N; round++)
        {
            // Complete one task WITHOUT settling it yet: the exact window the bug lived in.
            var live = engine.Snapshot.Where(t => t.State == DownloadTaskState.Pending).ToArray();
            if (live.Length > 0) live[rnd.Next(live.Length)].State = DownloadTaskState.Completed;
            BackupQueueBuilder.ReplanWholeQueue(store, devices, engine);
            worstDuplicates = Math.Max(worstDuplicates, DuplicateFileKeys(engine));
        }
        Assert.Equal(0, worstDuplicates, "a file was in the engine twice: it would download twice");
    }

    private sealed class BlockingHost : NullBackupHost
    {
        public readonly ManualResetEventSlim Release = new(false);
        public readonly ManualResetEventSlim Entered = new(false);
        public int Settled;
        public int OnCallerThread;
        public int CallerThread;
        public override void TaskSettled(DownloadTask task, DownloadSettlement.Outcome outcome)
        {
            if (Environment.CurrentManagedThreadId == Volatile.Read(ref CallerThread)) Interlocked.Increment(ref OnCallerThread);
            Entered.Set();
            Release.Wait(TimeSpan.FromSeconds(2));
            Interlocked.Increment(ref Settled);
        }
    }

    /// <summary>(Walk 09-25) An engine call from the UI thread ran the settle pass INLINE and waited behind a worker's
    /// notify (a drive probe on a busy stick): 5 to 24 s frozen. The settler now owns one worker; the caller only
    /// signals it. Held here with a host that blocks in TaskSettled: a queue-sized cancel must still return at once,
    /// and nothing the blocked settler owed is lost once it is released.</summary>
    [Test]
    public async Task An_engine_call_never_waits_for_the_settler()
    {
        var (store, engine) = Rig();
        var host = new BlockingHost { CallerThread = Environment.CurrentManagedThreadId };
        using var settler = new RunSettler(store, engine, null, host);
        BackupQueueBuilder.ReplanWholeQueue(store, new[] { new DeviceSpace("p", 100_000, true) }, engine);

        var done = engine.Snapshot.Take(5).ToList();
        foreach (var t in done) t.State = DownloadTaskState.Completed;
        // One cancel raises TaskChanged: the settler wakes, settles the five and blocks delivering the first.
        engine.CancelRange(engine.Snapshot.Where(t => t.State == DownloadTaskState.Pending).Take(1).ToList());
        Assert.True(host.Entered.Wait(TimeSpan.FromSeconds(5)), "the settler reached the blocking host");

        // A sixth file lands: the old inline settle would block THIS thread in TaskSettled for the call below.
        engine.Snapshot.First(t => t.State == DownloadTaskState.Pending).State = DownloadTaskState.Completed;
        // Ordering, not the clock: the call comes back while the host is still blocked (nothing settled through yet).
        int n = engine.CancelRange(engine.Snapshot.Where(t => t.State == DownloadTaskState.Pending).ToList());
        Assert.True(n > 100, "a queue-sized cancel");
        Assert.False(host.Release.IsSet, "the settler is still held when the call comes back");
        Assert.Equal(0, host.Settled, "nothing was delivered yet: the call did not wait behind the settler");

        host.Release.Set();
        await settler.FlushAsync();
        Assert.Equal(6, host.Settled, "every completed file was settled and delivered after the block cleared");
        Assert.Equal(0, host.OnCallerThread, "no settle ever ran on the caller's thread");
    }

    /// <summary>Interleaving a re-plan with settling must not lose queue entries either. Whatever is neither
    /// completed nor settled is still owed, and the run must still know about it.</summary>
    [Test]
    public void Re_planning_never_drops_a_file_that_is_still_owed()
    {
        var (store, engine) = Rig();
        var devices = new[] { new DeviceSpace("p", 100_000, true) };
        BackupQueueBuilder.ReplanWholeQueue(store, devices, engine);

        // Squeeze the drive to nothing and back, repeatedly: everything leaves the engine and comes back.
        for (int i = 0; i < 20; i++)
            BackupQueueBuilder.ReplanWholeQueue(store, i % 2 == 0 ? new[] { new DeviceSpace("p", 1, true) } : devices, engine);

        Assert.Equal(N, store.Current.Downloads.Snapshot().Count, "every file is still queued");
        Assert.Equal(N, engine.Snapshot.Count(t => t.State == DownloadTaskState.Pending), "and back in the engine");
        Assert.Equal(0, DuplicateFileKeys(engine), "with no duplicates after 20 round trips");
    }
}
