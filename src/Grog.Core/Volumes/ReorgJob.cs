// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Grog.Core.Volumes;

/// <summary>Why a reorg is running -- for the resume prompt. Layout = extras by-type/by-game switch within a
/// folder; ChangeFolder = a backup folder's address changed (same root, same relative paths); Relocate =
/// files move to a different root, rewriting RootId. Persisted as a string in reorg.json: renaming a member
/// orphans an in-flight job from an older build (Load() returns null; files and manifest untouched).</summary>
public enum ReorgReason { Layout = 0, ChangeFolder = 1, Relocate = 2 }

/// <summary>A persisted, resumable move job: the whole plan plus each move's live state. This is the
/// crash-safe source of truth; the manifest is kept in step with it as moves settle.</summary>
public sealed class ReorgJob
{
    /// <summary>Journal shape version (09-08); a newer build refuses to resume a job it cannot read.</summary>
    public const int CurrentSchema = 1;
    public int Schema { get; set; } = CurrentSchema;
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public ReorgReason Reason { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<ReorgMove> Moves { get; set; } = new();

    /// <summary>The move queue's Paused fact, persisted so a paused reorg stays paused across navigation and
    /// restart. The runner honors it; the UI derives from it. Distinct from the transient ReorgControl signal.</summary>
    public bool Paused { get; set; }

    /// <summary>(QA 09-30 B2) A Change Folder job's follow-up, persisted with the plan: once every move settles, this
    /// root's PathHint flips to <see cref="RepointPath"/>. Before, the flip lived only in an in-memory hook, so a
    /// job finished after a restart left every file reading Missing at the old folder.</summary>
    public string? RepointRootId { get; set; }
    public string? RepointPath { get; set; }

    /// <summary>The user stopped this job part-way and has not yet said what to do. The journal is kept:
    /// the two honest ways out (revert, or discard as orphans) both need the plan.</summary>
    [JsonPropertyName("Cancelled")]   // on-disk name pinned: existing journals must still resume
    public bool Canceled { get; set; }

    /// <summary>Guards <see cref="Moves"/> against concurrent reorder (UI thread) vs the runner's
    /// pick-next + journal serialization (worker thread). Not persisted.</summary>
    [JsonIgnore] public object SyncRoot { get; } = new();

    /// <summary>Drives this job needs that are offline/missing. When non-empty the caller must block the
    /// entire job -- a partial move silently omitting unreachable files is forbidden. Deduped display
    /// labels; recomputed per plan, never persisted.</summary>
    [JsonIgnore] public List<string> UnavailableDrives { get; } = new();

    /// <summary>True when one or more drives this job needs are offline/missing. The caller refuses to
    /// start the move and tells the user which drive to reconnect.</summary>
    [JsonIgnore] public bool HasUnavailableDrives => UnavailableDrives.Count > 0;

    /// <summary>Files the planner left out because they cannot land on the target (no room, or over the drive's
    /// per-file limit). The caller tells the user; a file that cannot fit is never queued. Not persisted.</summary>
    [JsonIgnore] public List<ReorgSkipped> NotFitting { get; } = new();

    /// <summary>Records a drive as unavailable for this job (deduped, case-insensitive on the label).</summary>
    public void NoteUnavailableDrive(string? label)
    {
        var name = string.IsNullOrWhiteSpace(label) ? "a backup drive" : label!;
        if (!UnavailableDrives.Contains(name, StringComparer.OrdinalIgnoreCase))
            UnavailableDrives.Add(name);
    }


    /// <summary>The move the runner is currently working (the "active" one, shown as a card in the UI).
    /// Set by the runner as it advances; not persisted.</summary>
    [JsonIgnore] public ReorgMove? Current { get; set; }

    public int Total => Moves.Count;
    public int Settled => Moves.Count(m => m.IsSettled);
    public bool IsComplete => Moves.All(m => m.IsSettled);
    public bool HasFailures => Moves.Any(m => m.State == ReorgMoveState.Failed);
    public long BytesTotal => Moves.Sum(m => m.SizeBytes);
    public long BytesSettled => Moves.Where(m => m.IsSettled).Sum(m => m.SizeBytes);

    /// <summary>(09-22) Add a freshly planned job's moves to this RUNNING job. Only another Relocate plan joins a
    /// Relocate job; a file already waiting here is not added twice. Returns the moves actually added (empty =
    /// nothing new). The runner picks new Pending moves up on its next pick.</summary>
    public List<ReorgMove> TryAppend(ReorgJob planned)
    {
        var added = new List<ReorgMove>();
        if (Reason != ReorgReason.Relocate || planned.Reason != ReorgReason.Relocate) return added;
        lock (SyncRoot)
        {
            var waiting = Moves.Where(x => !x.IsSettled).Select(x => (x.GogId, x.FileKey)).ToHashSet();
            foreach (var mv in planned.Moves)
                if (waiting.Add((mv.GogId, mv.FileKey))) { Moves.Add(mv); added.Add(mv); }
        }
        return added;
    }

    /// <summary>(09-22) What this job still has to WRITE, per target file: cross-volume copies not yet settled,
    /// less what is already copied. Same-volume renames write nothing. Feeds <see cref="Volumes.PendingWrites"/>.</summary>
    public List<(string TargetPath, long Bytes)> RemainingWrites()
    {
        lock (SyncRoot)
            return Moves.Where(x => !x.IsSettled && x.Kind == ReorgMoveKind.CopyVerifyDelete)
                        .Select(x => (x.ToAbs, Math.Max(0L, x.SizeBytes - x.CopiedBytes))).ToList();
    }

    /// <summary>Files not yet settled (the rail's "moving N files").</summary>
    public int Unsettled { get { lock (SyncRoot) return Moves.Count(x => !x.IsSettled); } }

    /// <summary>Moves not yet started (reorderable), in processing order.</summary>
    public IReadOnlyList<ReorgMove> Pending()
    {
        lock (SyncRoot) return Moves.Where(m => m.State == ReorgMoveState.Pending).ToList();
    }
}

/// <summary>One game to pin to a drive when its move job reaches the end (persisted in reorg.json).</summary>

/// <summary>A file the planner left out of a move and why (shown to the user, never persisted).</summary>
public sealed record ReorgSkipped(string Title, string FileName, long SizeBytes, string Reason);

/// <summary>What the target drive can take: free bytes after pending writes, and its per-file ceiling
/// (4 GiB on FAT32). The planner excludes cross-volume moves that cannot fit; renames never need room.</summary>
public sealed record MoveRoom(long FreeBytes, long MaxFileBytes, long ClusterBytes = 0)
{
    /// <summary>What <paramref name="bytes"/> cost the target volume (size rounded to its allocation unit).</summary>
    public long Charge(long bytes) => Grog.Core.Storage.DriveResolver.OnDisk(bytes, ClusterBytes);
    /// <summary>Reason text for a file over the per-file limit.</summary>
    public string LimitReason => $"over this drive's per-file limit ({Format.ByteFormat.Size(MaxFileBytes)})";
    public const string NoRoomReason = "not enough room";
}
