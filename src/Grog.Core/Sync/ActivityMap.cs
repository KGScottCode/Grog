// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using Grog.Core.Download;
using Grog.Core.Models;
using Grog.Core.Volumes;

namespace Grog.Core.Sync;

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

public readonly record struct FileRef(long GogId, string FileKey);

public static class ActivityMap
{
    public static IReadOnlyDictionary<FileRef, FileActivity> Derive(
        DownloadQueue downloads,
        IEnumerable<DownloadTask>? liveDownloads,
        IEnumerable<ReorgMove> reorgMoves,
        bool movePaused = false)
    {
        var map = new Dictionary<FileRef, FileActivity>();

        // 1. Download queue membership (persisted)
        if (downloads is not null)
            foreach (var q in downloads.Snapshot())
                map[new FileRef(q.GogId, q.FileKey)] =
                    downloads.Paused ? FileActivity.DownloadPaused : FileActivity.QueuedToDownload;

        // 2. Live engine refinement
        bool dlPaused = downloads?.Paused ?? false;
        foreach (var t in liveDownloads ?? Enumerable.Empty<DownloadTask>())
        {
            if (t.State != DownloadTaskState.Active) continue;
            var key = new FileRef(t.File.GameGogId, t.File.FileKey);
            map[key] = dlPaused ? FileActivity.DownloadPaused : FileActivity.Downloading;
        }

        // 3. Move queue (persisted reorg journal)
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
            // A file shouldn't be both downloading and moving
            map[new FileRef(m.GogId, m.FileKey)] = a;
        }

        return map;
    }

    public static FileActivity ForFile(FileRef file, IReadOnlyDictionary<FileRef, FileActivity> map)
        => map.TryGetValue(file, out var a) ? a : FileActivity.Idle;

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
