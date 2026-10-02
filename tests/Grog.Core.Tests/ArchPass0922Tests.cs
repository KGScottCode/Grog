// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;
using Grog.Core.Download;
using Grog.Core.Runs;
using Grog.Core.Volumes;
using Grog.Core.Tests.Framework;

namespace Grog.Core.Tests;

/// <summary>(09-22) The rules the architecture pass moved out of the App into Core: the move ledger as an
/// instance, a running move taking more files, pins that live on the job, the finish-current-files state, the
/// typed failure kind, and the fitting count Resume reads.</summary>
[NewBatch]
[Trait("arch")]
public sealed class ArchPass0922Tests
{
    private static ReorgMove Mv(long id, string key, ReorgMoveKind kind = ReorgMoveKind.CopyVerifyDelete, long size = 100, long copied = 0,
                                ReorgMoveState state = ReorgMoveState.Pending, string to = "/b/x")
        => new() { GogId = id, FileKey = key, Kind = kind, SizeBytes = size, CopiedBytes = copied, State = state, ToAbs = to };

    [Test] public void Two_ledgers_never_see_each_other()
    {
        var a = new PendingWrites(); var b = new PendingWrites();
        var dir = Path.GetTempPath();
        a.Register(() => new[] { (Path.Combine(dir, "f"), 500L) });
        Assert.Equal(500L, a.ForVolume(dir), "registered ledger counts its bytes");
        Assert.Equal(0L, b.ForVolume(dir), "an unrelated ledger is empty (no static state)");
        a.Clear();
        Assert.Equal(0L, a.ForVolume(dir), "cleared");
    }

    [Test] public void A_running_move_takes_only_new_files()
    {
        var run = new ReorgJob { Reason = ReorgReason.Relocate };
        run.Moves.Add(Mv(1, "a")); run.Moves.Add(Mv(1, "done", state: ReorgMoveState.Done));
        var plan = new ReorgJob { Reason = ReorgReason.Relocate };
        plan.Moves.Add(Mv(1, "a")); plan.Moves.Add(Mv(2, "b")); plan.Moves.Add(Mv(1, "done"));
        var added = run.TryAppend(plan);
        Assert.Equal(2, added.Count, "b joins; a is already waiting; a settled file may move again");
        Assert.Equal(4, run.Moves.Count, "job grew by what was added");
        var layout = new ReorgJob { Reason = ReorgReason.Layout }; layout.Moves.Add(Mv(3, "c"));
        Assert.Equal(0, run.TryAppend(layout).Count, "a layout plan never joins a relocate job");
    }

    [Test] public void Remaining_writes_are_unsettled_cross_volume_copies_less_what_is_copied()
    {
        var j = new ReorgJob { Reason = ReorgReason.Relocate };
        j.Moves.Add(Mv(1, "a", size: 100, copied: 30));
        j.Moves.Add(Mv(1, "b", kind: ReorgMoveKind.Rename, size: 100));
        j.Moves.Add(Mv(1, "c", size: 100, state: ReorgMoveState.Done));
        var w = j.RemainingWrites();
        Assert.Equal(1, w.Count, "only the unsettled copy writes");
        Assert.Equal(70L, w[0].Bytes, "less what is already on the target");
        Assert.Equal(2, j.Unsettled, "a and b are unsettled");
    }

    [Test] public void Finish_current_files_is_one_state()
    {
        var d = new RunDrain();
        Assert.False(d.Request(), "no live run: nothing to finish");
        d.RunBegan();
        Assert.True(d.CanRequest, "live run can be asked");
        var e = new DownloadEngine(null!, null!);
        Assert.True(d.Request(), "asked before the engine exists");
        Assert.False(d.Request(), "asking twice is a no-op");
        d.EngineReady(e);
        Assert.True(e.StartNothingNew, "applied the moment the engine exists");
        d.EndedPaused(); d.RunOver();
        Assert.True(d.PausedAfterFinish, "the reason for the pause outlives the run");
        d.RunBegan();
        Assert.Equal(DrainState.None, d.State, "a new run starts clean");
        d.Request(); d.RunOver();
        Assert.Equal(DrainState.None, d.State, "a finish that never paused ends with its run");
    }

    [Test] public void Keep_downloading_takes_the_finish_back()
    {
        var d = new RunDrain();
        d.RunBegan();
        Assert.True(d.Request(), "asked");
        Assert.True(d.Cancel(), "taken back before the engine exists");
        Assert.Equal(DrainState.None, d.State, "nothing left of it");
        var e = new DownloadEngine(null!, null!);
        d.EngineReady(e);
        Assert.False(e.StartNothingNew, "the engine never saw it");
        d.Request();
        Assert.False(d.Cancel(), "an engine that is not running cannot take files again: too late");
        Assert.True(d.Finishing, "so the finish stands");
        d.RunOver();
        Assert.False(d.Cancel(), "no live run");
    }

    [Test] public void Failure_kind_is_typed_not_read_from_the_message()
    {
        Assert.Equal(DownloadFailureKind.Network, DownloadFailure.Of(new StallException(TimeSpan.FromSeconds(30))), "stall");
        Assert.Equal(DownloadFailureKind.Network, DownloadFailure.Of(new HttpRequestException("x", new SocketException())), "dns/socket");
        Assert.Equal(DownloadFailureKind.Network, DownloadFailure.Of(new IOException("wrap", new SocketException())), "socket inside IO");
        Assert.Equal(DownloadFailureKind.Other, DownloadFailure.Of(new IOException("the connection word alone means nothing")), "no text sniffing");
        Assert.Equal(DownloadFailureKind.Network, DownloadFailure.Of(new TaskCanceledException("resolve timed out")), "a timeout that reaches a failure is the link (1283)");
        Assert.Equal(DownloadFailureKind.Network, DownloadFailure.Of(new StallException("No response from the download server within 30 s")), "header stall (1283)");
    }

    [Test] public void The_journal_says_connection_lost_from_the_kind()
    {
        var dir = Path.Combine(Path.GetTempPath(), "grog-kind-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            using (var j = RunJournal.Start(dir, "backup", 2)!)
            {
                j.File("a", ok: false, 0, "Failed", "Stalled: no data for 30s", kind: "network");
                j.File("b", ok: false, 0, "Failed", "boom", kind: "other");
            }
            var r = RunJournalReader.ReadLastFailure(dir);
            Assert.True(r is { Failed: 2, ConnectionLost: true }, "first failure's kind decides");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Test] public void Resume_counts_only_files_that_fit()
    {
        var q = new DownloadQueue();
        q.Enqueue(1, "a", "p", fits: true);
        q.Enqueue(1, "b", "p", fits: false);
        q.Enqueue(2, "c", "p", fits: true);
        Assert.Equal(2, q.FittingCount, "red rows are not resumable");
        Assert.True(q.AnyWontFit, "one red row");
        Assert.Equal("b", q.WontFit().Single().FileKey, "the red row, in order");
    }
}
