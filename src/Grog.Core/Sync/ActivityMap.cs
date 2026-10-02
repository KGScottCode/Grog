// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using Grog.Core.Download;
using Grog.Core.Models;
using Grog.Core.Volumes;

namespace Grog.Core.Sync;

/// <summary>The transient "what is happening / scheduled" axis for one file. DERIVED, never stored:
/// a crash or a missed event can't leave a file stuck here because it's recomputed from the live
/// download engine snapshot + the reorg journal every time. <see cref="Active"/> is a game-level-only
/// value (two or more distinct activities on one game at once); a single file is never "Active".</summary>
public enum FileActivity
{
    Idle = 0,
    QueuedToDownload = 1,
    Downloading = 2,
    DownloadPaused = 3,
    QueuedToMove = 4,
    Moving = 5,
    MovePaused = 6,
    Verifying = 7,
    Active = 8,          // game rollup only: mixed activities in play
}

/// <summary>Identity of a file across the manifest: a FileKey is unique within a game, so a game id +
/// key names exactly one file.</summary>
public readonly record struct FileRef(long GogId, string FileKey);

/// <summary>
/// Activity as a pure, derived read: maps each file to its current Activity from the download-engine
/// snapshot and the reorg journal. Nothing is persisted; feed it fresh inputs and it self-heals.
/// Displayed status (elsewhere) = Activity if not Idle, else Condition.
/// </summary>
public static class ActivityMap
{
    /// <summary>Derive the file-to-Activity map from the PERSISTED queues (the source of truth), refined by
    /// the live engine for in-flight state. A completed download is removed from the queue by the complete
    /// Action and simply falls out of the map, so no transient flag can leave a file stuck.</summary>
    /// <param name="downloads">The persisted download queue (ordered files + its Paused flag).</param>
    /// <param name="liveDownloads">The live engine snapshot, for in-flight refinement (may be empty/null
    /// when nothing is running -- then queued files read QueuedToDownload / DownloadPaused from the queue).</param>
    /// <param name="reorgMoves">The move queue: the reorg journal's moves (already persisted).</param>
    /// <param name="movePaused">The move queue's Paused flag (from the reorg journal).</param>
    public static IReadOnlyDictionary<FileRef, FileActivity> Derive(
        DownloadQueue downloads,
        IEnumerable<DownloadTask>? liveDownloads,
        IEnumerable<ReorgMove> reorgMoves,
        bool movePaused = false)
    {
        var map = new Dictionary<FileRef, FileActivity>();

        // 1. Download queue membership (persisted): paused -> DownloadPaused, else QueuedToDownload.
        //    Snapshot() so a concurrent enqueue/remove on the download thread can't crash enumeration.
        if (downloads is not null)
            foreach (var q in downloads.Snapshot())
                map[new FileRef(q.GogId, q.FileKey)] =
                    downloads.Paused ? FileActivity.DownloadPaused : FileActivity.QueuedToDownload;

        // 2. Live engine refinement: a queued file the engine is actively transferring reads Downloading
        //    (unless the queue is paused, in which case it stays frozen as DownloadPaused). Settled tasks
        //    are ignored -- the complete Action removes them from the queue.
        bool dlPaused = downloads?.Paused ?? false;
        foreach (var t in liveDownloads ?? Enumerable.Empty<DownloadTask>())
        {
            if (t.State != DownloadTaskState.Active) continue;
            var key = new FileRef(t.File.GameGogId, t.File.FileKey);
            map[key] = dlPaused ? FileActivity.DownloadPaused : FileActivity.Downloading;
        }

        // 3. Move queue (persisted reorg journal). Pending -> QueuedToMove (or MovePaused when the queue is
        //    paused); Copied -> Moving; Verified -> Verifying. Settled moves (Done/Skipped/Failed) drop out.
        foreach (var m in reorgMoves ?? Enumerable.Empty<ReorgMove>())
        {
            var a = m.State switch
            {
                ReorgMoveState.Pending => movePaused ? FileActivity.MovePaused : FileActivity.QueuedToMove,
                ReorgMoveState.Copied => FileActivity.Moving,     // bytes in flight, not yet verified
                ReorgMoveState.Verified => FileActivity.Verifying,// copied, verifying/promoting before source delete
                _ => FileActivity.Idle,   // Done / Skipped / Failed
            };
            if (a == FileActivity.Idle) continue;
            // A file shouldn't be both downloading and moving (the activity gate serializes them), but if
            // both name it, the move wins and the game rollup still resolves to Active.
            map[new FileRef(m.GogId, m.FileKey)] = a;
        }

        return map;
    }

    /// <summary>Activity for one file, or Idle when it isn't doing anything.</summary>
    public static FileActivity ForFile(FileRef file, IReadOnlyDictionary<FileRef, FileActivity> map)
        => map.TryGetValue(file, out var a) ? a : FileActivity.Idle;

    /// <summary>Activity for a whole game: one activity type in play shows itself; two or more DISTINCT
    /// types at once roll up to <see cref="FileActivity.Active"/>. Many files doing the SAME thing is
    /// still that one thing, not Active.</summary>
    public static FileActivity ForGame(long gogId, IEnumerable<GameFile> files,
        IReadOnlyDictionary<FileRef, FileActivity> map)
    {
        FileActivity? single = null;
        foreach (var f in files)
        {
            if (!map.TryGetValue(new FileRef(gogId, f.FileKey), out var a) || a == FileActivity.Idle) continue;
            if (single is null) single = a;
            else if (single != a) return FileActivity.Active;   // two distinct kinds -> generic
        }
        return single ?? FileActivity.Idle;
    }
}
