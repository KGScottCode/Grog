// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System.Linq;
using Grog.Core.Download;
using Grog.Core.Models;
using Grog.Core.Tests.Framework;

/// <summary>The queue IS the priority: position decides what runs next, and dragging something to the
/// front pre-empts what's running. Pre-emption is only cheap because partials survive in .grog-tmp.</summary>
[NewBatch]
[Trait("queue")]
public class DownloadQueueTests
{
    static DownloadTask T(string name) => new()
    {
        File = new GameFile { Name = name, Kind = FileKind.Installer },
        GameTitle = name,
    };

    static DownloadEngine NewEngine() => new(null!, null!) { MaxConcurrent = 1 };

    [Test] void Enqueue_KeepsInsertionOrder()
    {
        var e = NewEngine();
        var a = T("a"); var b = T("b"); var c = T("c");
        e.Enqueue(a); e.Enqueue(b); e.Enqueue(c);
        Assert.Equal("a,b,c", string.Join(",", e.Snapshot.Select(t => t.GameTitle)), "queue starts in the order added");
    }

    [Test] void MoveTo_Front_ReordersQueue()
    {
        var e = NewEngine();
        var a = T("a"); var b = T("b"); var c = T("c");
        e.Enqueue(a); e.Enqueue(b); e.Enqueue(c);
        e.MoveTo(c, 0);
        Assert.Equal("c,a,b", string.Join(",", e.Snapshot.Select(t => t.GameTitle)), "dragged to the front");
    }

    [Test] void MoveTo_Middle_ReordersQueue()
    {
        var e = NewEngine();
        var a = T("a"); var b = T("b"); var c = T("c");
        e.Enqueue(a); e.Enqueue(b); e.Enqueue(c);
        e.MoveTo(a, 2);
        Assert.Equal("b,c,a", string.Join(",", e.Snapshot.Select(t => t.GameTitle)), "dragged to the back");
    }

    [Test] void MoveTo_PreemptsTheRunningTask()
    {
        var e = NewEngine();   // one slot
        var running = T("running"); var jumper = T("jumper");
        e.Enqueue(running); e.Enqueue(jumper);
        running.State = DownloadTaskState.Active;
        running.Cancellation = new System.Threading.CancellationTokenSource();

        e.MoveTo(jumper, 0);   // jumper takes the only slot

        Assert.True(running.PreemptRequested, "the running task is told it's been bumped");
        Assert.True(running.Cancellation!.IsCancellationRequested, "and its transfer is stopped");
        Assert.Equal("jumper,running", string.Join(",", e.Snapshot.Select(t => t.GameTitle)), "order reflects the drag");
    }

    // Walk 09-25: Size sort on the FAT32 stick left a 4 GB part downloading at the bottom of the queue for a minute
    // while the 1 MB files sorted to the top waited. The stick was judged slow (one file at a time), so every
    // Pending row above it was "gated" behind that very Active task and counted as nothing ahead of it.
    static DownloadTask K(string name, string root) => new()
    {
        File = new GameFile { Name = name, Kind = FileKind.Installer, GameGogId = 7, FileKey = "/k/" + name },
        GameTitle = name, TargetRootId = root,
    };

    [Test] void Reorder_on_a_slow_drive_preempts_the_demoted_download()
    {
        var mon = new DeviceWriteMonitor(); mon.Record("stick", 17);
        var e = new DownloadEngine(null!, null!) { MaxConcurrent = 2, DeviceMonitor = mon };
        var big = K("big", "stick"); var s1 = K("s1", "stick"); var s2 = K("s2", "stick");
        e.Enqueue(big); e.Enqueue(s1); e.Enqueue(s2);
        big.State = DownloadTaskState.Active;
        big.Cancellation = new System.Threading.CancellationTokenSource();

        e.Reorder(new[] { s1, s2, big }.Select(t => (t.File.GameGogId, t.File.FileKey)).ToList());

        Assert.True(big.PreemptRequested, "a re-sort preempts what it demotes, slow drive or not");
        Assert.Equal("s1,s2,big", string.Join(",", e.Snapshot.Select(t => t.GameTitle)), "engine follows the sort");
    }

    [Test] void Reorder_on_a_slow_drive_keeps_one_file_per_drive_in_the_window()
    {
        // Two workers, one slow drive: the window on that drive is ONE file. A second Active on it (started before
        // the drive was judged slow) that the sort puts behind another row on the same drive is preempted.
        var mon = new DeviceWriteMonitor(); mon.Record("stick", 17);
        var e = new DownloadEngine(null!, null!) { MaxConcurrent = 2, DeviceMonitor = mon };
        var a = K("a", "stick"); var b = K("b", "stick");
        e.Enqueue(a); e.Enqueue(b);
        foreach (var t in new[] { a, b }) { t.State = DownloadTaskState.Active; t.Cancellation = new System.Threading.CancellationTokenSource(); }
        var c = K("c", "stick"); e.Enqueue(c);

        e.Reorder(new[] { a, c, b }.Select(t => (t.File.GameGogId, t.File.FileKey)).ToList());

        Assert.True(!a.PreemptRequested, "the top row keeps its transfer");
        Assert.True(b.PreemptRequested, "the demoted second file on the slow drive stops");
    }

    [Test] void Reorder_never_preempts_a_file_that_is_writing_verifying_or_saving()
    {
        // Owner 09-25: every byte is in; stopping it now only throws away the flush or the hash.
        foreach (var phase in new[] { "Writing to disk", "Verifying", "Saving" })
        {
            var e = new DownloadEngine(null!, null!) { MaxConcurrent = 1 };
            var big = K("big", "hd"); var s1 = K("s1", "hd");
            e.Enqueue(big); e.Enqueue(s1);
            big.State = DownloadTaskState.Active; big.FinishingPhase = phase;
            big.Cancellation = new System.Threading.CancellationTokenSource();

            e.Reorder(new[] { s1, big }.Select(t => (t.File.GameGogId, t.File.FileKey)).ToList());

            Assert.True(!big.PreemptRequested && !big.Cancellation.IsCancellationRequested, phase + ": it finishes");
            Assert.Equal("s1,big", string.Join(",", e.Snapshot.Select(t => t.GameTitle)), phase + ": order still follows the sort");
        }
    }

    [Test] void Waiting_rows_on_a_slow_drive_never_bump_a_fast_drive_download()
    {
        // QA 09-25: two stick rows sorted on top take ONE slot between them (the stick runs one file), so the hard
        // drive's download keeps the other slot instead of being cut and picked straight back up.
        var mon = new DeviceWriteMonitor(); mon.Record("stick", 17);
        var e = new DownloadEngine(null!, null!) { MaxConcurrent = 2, DeviceMonitor = mon };
        var h = K("h", "hd"); var k = K("k", "stick"); var s1 = K("s1", "stick"); var s2 = K("s2", "stick");
        e.Enqueue(h); e.Enqueue(k); e.Enqueue(s1); e.Enqueue(s2);
        foreach (var t in new[] { h, k }) { t.State = DownloadTaskState.Active; t.Cancellation = new System.Threading.CancellationTokenSource(); }

        e.Reorder(new[] { s1, s2, h, k }.Select(t => (t.File.GameGogId, t.File.FileKey)).ToList());

        Assert.True(!h.PreemptRequested, "the hard drive's file keeps its slot");
        Assert.True(k.PreemptRequested, "the demoted stick file gives the stick to s1");
    }

    [Test] void Two_stick_files_already_running_are_not_bumped_when_nothing_waits_above_them()
    {
        // QA 09-25: both started before the stick was judged slow. Raising the limit (or a drag elsewhere) re-runs
        // the window; nothing on the stick ranks above the second, so nothing stops.
        var mon = new DeviceWriteMonitor(); mon.Record("stick", 17);
        var e = new DownloadEngine(null!, null!) { MaxConcurrent = 2, DeviceMonitor = mon };
        var a = K("a", "stick"); var b = K("b", "stick"); var c = K("c", "hd");
        e.Enqueue(a); e.Enqueue(b); e.Enqueue(c);
        foreach (var t in new[] { a, b }) { t.State = DownloadTaskState.Active; t.Cancellation = new System.Threading.CancellationTokenSource(); }

        e.Reorder(new[] { a, b, c }.Select(t => (t.File.GameGogId, t.File.FileKey)).ToList());
        e.MoveTo(c, 2);

        Assert.True(!a.PreemptRequested && !b.PreemptRequested, "no waiting stick row ranks above either");
    }

    [Test] void Lowering_the_limit_counts_every_running_file_on_a_slow_drive()
    {
        // QA 09-25: two stick files and one hard-drive file running, limit 3 -> 2: three files on two slots is one too many.
        var mon = new DeviceWriteMonitor(); mon.Record("stick", 17);
        var e = new DownloadEngine(null!, null!) { MaxConcurrent = 3, DeviceMonitor = mon };
        var a = K("a", "stick"); var b = K("b", "stick"); var h = K("h", "hd");
        e.Enqueue(a); e.Enqueue(b); e.Enqueue(h);
        foreach (var t in new[] { a, b, h }) { t.State = DownloadTaskState.Active; t.Cancellation = new System.Threading.CancellationTokenSource(); }

        e.SetMaxConcurrent(2);

        Assert.True(h.PreemptRequested, "the third running file is outside the new window");
        Assert.True(!a.PreemptRequested && !b.PreemptRequested, "the first two keep their slots");
    }

    [Test] void Reorder_on_a_fast_drive_still_keeps_the_whole_window()
    {
        var e = new DownloadEngine(null!, null!) { MaxConcurrent = 2 };
        var a = K("a", "hd"); var b = K("b", "hd"); var c = K("c", "hd");
        e.Enqueue(a); e.Enqueue(b); e.Enqueue(c);
        foreach (var t in new[] { a, b }) { t.State = DownloadTaskState.Active; t.Cancellation = new System.Threading.CancellationTokenSource(); }

        e.Reorder(new[] { a, c, b }.Select(t => (t.File.GameGogId, t.File.FileKey)).ToList());

        Assert.True(b.PreemptRequested, "b fell to third behind two slots");
        Assert.True(!a.PreemptRequested, "a is still first");
    }

    [Test] void MoveTo_WithinActiveWindow_DoesNotPreempt()
    {
        var e = new DownloadEngine(null!, null!) { MaxConcurrent = 3 };
        var a = T("a"); var b = T("b"); var c = T("c");
        e.Enqueue(a); e.Enqueue(b); e.Enqueue(c);
        a.State = DownloadTaskState.Active;
        a.Cancellation = new System.Threading.CancellationTokenSource();

        e.MoveTo(c, 0);   // still inside the 3 active slots

        Assert.True(!a.PreemptRequested, "nothing was pushed out of the window, so nothing is interrupted");
    }

    [Test] void Cancel_Pending_LeavesTheQueue()
    {
        var e = NewEngine();
        var a = T("a"); var b = T("b");
        e.Enqueue(a); e.Enqueue(b);
        e.Cancel(b);
        Assert.Equal(DownloadTaskState.Canceled, b.State, "a waiting file just leaves");
        Assert.Equal(1, e.QueuedCount, "and stops counting as queued");
        Assert.False(e.Snapshot.Contains(b), "a canceled PENDING task leaves the engine's queue, never lingers as a ghost");
        Assert.Equal(1, e.TotalCount, "and the run total follows it out");
    }

    [Test] void Cancel_Active_StopsThatTransferOnly()
    {
        var e = NewEngine();
        var a = T("a"); var b = T("b");
        e.Enqueue(a); e.Enqueue(b);
        a.State = DownloadTaskState.Active;
        a.Cancellation = new System.Threading.CancellationTokenSource();

        e.Cancel(a);

        Assert.True(a.Cancellation!.IsCancellationRequested, "the running file stops");
        Assert.Equal(DownloadTaskState.Pending, b.State, "the next one is untouched");
    }

    [Test] void QueuedCount_IgnoresFinishedWork()
    {
        var e = NewEngine();
        var a = T("a"); var b = T("b"); var c = T("c");
        e.Enqueue(a); e.Enqueue(b); e.Enqueue(c);
        a.State = DownloadTaskState.Completed;
        b.State = DownloadTaskState.Failed;
        Assert.Equal(1, e.QueuedCount, "only pending/active count as queued");
    }
}
