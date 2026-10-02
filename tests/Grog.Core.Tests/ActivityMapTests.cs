// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System.Collections.Generic;
using Grog.Core.Download;
using Grog.Core.Models;
using Grog.Core.Sync;
using Grog.Core.Tests.Framework;
using Grog.Core.Volumes;

[NewBatch]
[Trait("activity")]
public class ActivityViewTests
{
    private static DownloadQueue Queue(bool paused, params (long gogId, string key)[] items)
    {
        var q = new DownloadQueue { Paused = paused };
        foreach (var (g, k) in items) q.Enqueue(g, k);
        return q;
    }

    private static DownloadTask Live(long gogId, string key, DownloadTaskState state)
        => new()
        {
            File = new GameFile { GameGogId = gogId, FileKey = key, Kind = FileKind.Installer },
            GameTitle = $"Game {gogId}",
            State = state,
        };

    private static ReorgMove Move(long gogId, string key, ReorgMoveState state)
        => new() { GogId = gogId, FileKey = key, State = state };

    private static GameFile File(string key) => new() { FileKey = key, Kind = FileKind.Installer };

    // ---- download queue is the source of truth ----------------------------------------------

    [Test] void QueuedFilesReadQueuedEvenWithNoLiveEngine()
    {
        // The whole point: membership survives with no engine running (post-restart), so a queued file
        // still shows QueuedToDownload rather than vanishing.
        var map = ActivityMap.Derive(Queue(false, (1, "a"), (1, "b")), null, System.Array.Empty<ReorgMove>());
        Assert.Equal(FileActivity.QueuedToDownload, ActivityMap.ForFile(new(1, "a"), map), "queued, no engine");
        Assert.Equal(FileActivity.QueuedToDownload, ActivityMap.ForFile(new(1, "b"), map), "queued, no engine");
    }

    [Test] void PausedQueueReadsDownloadPaused()
    {
        var map = ActivityMap.Derive(Queue(true, (1, "a")), null, System.Array.Empty<ReorgMove>());
        Assert.Equal(FileActivity.DownloadPaused, ActivityMap.ForFile(new(1, "a"), map), "paused queue");
    }

    [Test] void LiveEngineUpgradesQueuedToDownloading()
    {
        var map = ActivityMap.Derive(Queue(false, (1, "a"), (1, "b")),
            new[] { Live(1, "a", DownloadTaskState.Active) }, System.Array.Empty<ReorgMove>());
        Assert.Equal(FileActivity.Downloading, ActivityMap.ForFile(new(1, "a"), map), "active -> downloading");
        Assert.Equal(FileActivity.QueuedToDownload, ActivityMap.ForFile(new(1, "b"), map), "still queued");
    }

    [Test] void PausedQueueStaysFrozenEvenIfEngineStillActive()
    {
        // Pause is a fact on the queue; a live task that hasn't torn down yet must not override it.
        var map = ActivityMap.Derive(Queue(true, (1, "a")),
            new[] { Live(1, "a", DownloadTaskState.Active) }, System.Array.Empty<ReorgMove>());
        Assert.Equal(FileActivity.DownloadPaused, ActivityMap.ForFile(new(1, "a"), map), "queue paused wins");
    }

    [Test] void CompletedFileIsNoLongerInQueueSoIdle()
    {
        // The complete Action removes it from the queue; the derivation then naturally yields Idle.
        var map = ActivityMap.Derive(Queue(false, (1, "b")), null, System.Array.Empty<ReorgMove>());
        Assert.Equal(FileActivity.Idle, ActivityMap.ForFile(new(1, "a"), map), "not in queue -> idle");
    }

    // ---- move queue -------------------------------------------------------------------------

    [Test] void MoveStatesMapAndHonorMovePaused()
    {
        var moves = new[]
        {
            Move(1, "a", ReorgMoveState.Pending),
            Move(1, "b", ReorgMoveState.Copied),
            Move(1, "c", ReorgMoveState.Verified),
            Move(1, "d", ReorgMoveState.Done),
        };
        var map = ActivityMap.Derive(new DownloadQueue(), null, moves, movePaused: false);
        Assert.Equal(FileActivity.QueuedToMove, ActivityMap.ForFile(new(1, "a"), map), "pending move");
        Assert.Equal(FileActivity.Moving, ActivityMap.ForFile(new(1, "b"), map), "copied = moving");
        Assert.Equal(FileActivity.Verifying, ActivityMap.ForFile(new(1, "c"), map), "verified = verifying");
        Assert.Equal(FileActivity.Idle, ActivityMap.ForFile(new(1, "d"), map), "done -> idle");

        var paused = ActivityMap.Derive(new DownloadQueue(), null, moves, movePaused: true);
        Assert.Equal(FileActivity.MovePaused, ActivityMap.ForFile(new(1, "a"), paused), "pending + paused = MovePaused");
        Assert.Equal(FileActivity.Moving, ActivityMap.ForFile(new(1, "b"), paused), "an in-flight copy still moving");
    }

    // ---- game rollup ------------------------------------------------------------------------

    [Test] void GameAllQueuedIsQueued()
    {
        var map = ActivityMap.Derive(Queue(false, (1, "a"), (1, "b")), null, System.Array.Empty<ReorgMove>());
        Assert.Equal(FileActivity.QueuedToDownload, ActivityMap.ForGame(1, new[] { File("a"), File("b") }, map),
            "same activity across files stays that activity");
    }

    [Test] void GameDownloadingAndQueuedIsActive()
    {
        var map = ActivityMap.Derive(Queue(false, (1, "a"), (1, "b")),
            new[] { Live(1, "a", DownloadTaskState.Active) }, System.Array.Empty<ReorgMove>());
        Assert.Equal(FileActivity.Active, ActivityMap.ForGame(1, new[] { File("a"), File("b") }, map),
            "downloading + queued are two distinct types");
    }

    [Test] void GameDownloadingAndMovingIsActive()
    {
        var map = ActivityMap.Derive(Queue(false, (1, "a")),
            new[] { Live(1, "a", DownloadTaskState.Active) },
            new[] { Move(1, "b", ReorgMoveState.Copied) });
        Assert.Equal(FileActivity.Active, ActivityMap.ForGame(1, new[] { File("a"), File("b") }, map),
            "downloading + moving on one game -> Active");
    }

    [Test] void GameWithNoActivityIsIdle()
    {
        var map = ActivityMap.Derive(new DownloadQueue(), null, System.Array.Empty<ReorgMove>());
        Assert.Equal(FileActivity.Idle, ActivityMap.ForGame(1, new[] { File("a") }, map), "nothing happening");
    }
}
