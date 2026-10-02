// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grog.Core.Manifest;
using Grog.Core.Verify;

namespace Grog.Core.Volumes;

/// <summary>
/// Executes a <see cref="ReorgJob"/> safely and resumably: journaled per-file state machine, manifest
/// repathed only once the destination provably exists, renames same-drive, copy -> verify -> delete-source
/// cross-drive. Pause/cancel take effect between files AND between copy chunks (a multi-GB copy must not
/// ignore Pause for minutes); an interrupted copy discards its .grogpart at once, so resume just re-runs
/// the same job.
/// </summary>
public sealed class ReorgRunner
{
    private const string PartSuffix = ".grogpart";

    private readonly IManifestStore _store;
    private readonly ReorgJournalStore _journal;

    /// <summary>Fired as each file starts and finishes, and periodically during a cross-drive copy.</summary>
    public event Action<ReorgProgress>? Progress;

    public ReorgRunner(IManifestStore store, ReorgJournalStore journal)
    {
        _store = store;
        _journal = journal;
    }

    /// <summary>Loads a journal left behind by an interrupted run (for the resume prompt), or null.</summary>
    public ReorgJob? LoadPending() => _journal.Load();

    /// <summary>Is the storage under this absolute path reachable right now? Overridable for tests. The default asks
    /// the drive resolver (a pulled removable reads offline), not <c>Path.GetPathRoot</c>: on macOS and Linux that is
    /// always "/", so a pulled source drive read "reachable" and every remaining move was Skipped and repathed at
    /// files that were never there (QA 09-30 B1). The callers pass the STORAGE folder (the move's absolute path with
    /// its relative part stripped), so an unmounted mount point whose folder is gone reads offline on Linux too.</summary>
    public Func<string, bool> VolumeReachable { get; set; } = path =>
    {
        try
        {
            // The storage folder itself, else its nearest existing ancestor: a drive letter root ("G:\") says the
            // drive is present; the POSIX root ("/") says nothing and never counts.
            var probe = Path.GetFullPath(path);
            while (!Directory.Exists(probe))
            {
                var parent = Path.GetDirectoryName(probe);
                if (string.IsNullOrEmpty(parent)) return false;
                probe = parent;
            }
            if (!OperatingSystem.IsWindows() && probe == Path.GetPathRoot(probe)) return false;
            return Storage.DriveResolver.IsOnline(probe);
        }
        catch { return false; }
    };

    /// <summary>The storage folder a move's absolute path sits under: the path with its relative part stripped, or
    /// the path's own folder when the two do not line up (an old journal).</summary>
    internal static string StorageFolderOf(string abs, string rel) => StorageFolder(abs, rel).Folder;
    private static (string Folder, bool Exact) StorageFolder(string abs, string rel)
    {
        try
        {
            var full = Path.GetFullPath(abs);
            var relNorm = rel.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
            if (relNorm.Length > 0 && full.EndsWith(relNorm, StringComparison.OrdinalIgnoreCase) && full.Length > relNorm.Length)
                return (full[..^relNorm.Length].TrimEnd(Path.DirectorySeparatorChar), true);
            return (Path.GetDirectoryName(full) ?? full, false);
        }
        catch { return (abs, false); }
    }
    /// <summary>The storage folder itself is the fact when the move names it: gone (unplugged, unmounted, deleted)
    /// is unreachable, whatever ancestor still exists. A journal whose paths do not line up falls back to the probe.</summary>
    private bool Reachable(string abs, string rel)
    {
        var (folder, exact) = StorageFolder(abs, rel);
        return (!exact || Directory.Exists(folder)) && VolumeReachable(folder);
    }
    internal bool SourceReachableForTest(ReorgMove mv) => SourceReachable(mv);
    internal bool TargetReachableForTest(ReorgMove mv) => TargetReachable(mv);
    private bool SourceReachable(ReorgMove mv) => Reachable(mv.FromAbs, mv.FromRel);
    private bool TargetReachable(ReorgMove mv) => Reachable(mv.ToAbs, mv.ToRel);

    /// <summary>Failures in a row before the runner stops trying and pauses the job. A pulled drive fails every
    /// move in milliseconds; without this the whole tail is burned through (owner-hit 09-04: 43 files).</summary>
    public const int ConsecutiveFailurePause = 3;

    /// <summary>Runs (or resumes) the job to completion, or until paused/canceled. Settled moves are
    /// skipped, so calling this again on the same job resumes where it left off.</summary>
    public async Task<ReorgResult> RunAsync(ReorgJob job, ReorgControl control, CancellationToken ct = default)
    {
        var m = _store.Current;
        // Entering the loop means running, first start or resume alike: clear the persisted pause flag here
        // (Finalize sets it) so ReorgJob.Paused on disk always reflects reality.
        lock (job.SyncRoot)
        {
            job.Paused = false;
            // A start or resume re-admits earlier failures: a move that failed because a drive blinked out is
            // worth another go, and the user's Resume click is the retry (before 09-04 Failed moves were
            // never picked up again, whatever the comment said).
            foreach (var x in job.Moves)
                if (x.State == ReorgMoveState.Failed) { x.State = ReorgMoveState.Pending; x.Error = null; }
        }
        SaveJournal(job);   // ensure the plan is on disk before touching a single file

        var result = new ReorgResult();
        int consecutiveFailures = 0;

        // Pick the next move each iteration (not a fixed foreach) so the pending tail can be reordered live.
        // Failed moves wait for a later resume; the pick is guarded so it can't race a UI-thread reorder.
        while (true)
        {
            ReorgMove? mv;
            lock (job.SyncRoot)
            {
                mv = job.Moves.FirstOrDefault(x =>
                    x.State is ReorgMoveState.Pending or ReorgMoveState.Copied or ReorgMoveState.Verified);
                job.Current = mv;
            }
            if (mv is null) break;

            if (control.IsCanceled) { result.Outcome = ReorgOutcome.Canceled; break; }
            if (control.IsPaused) { result.Outcome = ReorgOutcome.Paused; break; }
            ct.ThrowIfCancellationRequested();

            RaiseProgress(job, mv, 0);
            try
            {
                await ExecuteMoveAsync(mv, job, control, ct);
            }
            catch (OperationCanceledException)
            {
                // Token cancel or a mid-copy pause/cancel: leave this move un-settled, keep the journal, stop.
                result.Outcome = control.IsCanceled ? ReorgOutcome.Canceled : ReorgOutcome.Paused;
                SaveJournal(job);
                await SaveManifestAsync(ct);
                lock (job.SyncRoot) job.Current = null;
                return Finalize(job, result);
            }
            catch (Exception ex)
            {
                mv.State = ReorgMoveState.Failed;
                mv.Error = ex.Message;
                consecutiveFailures++;
                // The BREAKER. A destination (or source) volume that is gone will fail every remaining move in
                // milliseconds; marching on turns one unplugged cable into a queue of failures. Put this move
                // back (nothing landed: the copy never started or its .part was discarded), pause the job and
                // say why. Resume is the retry, once the drive is back.
                string? why = !TargetReachable(mv) ? "the destination drive is not reachable"
                    : !SourceReachable(mv) ? "the source drive is not reachable"
                    : consecutiveFailures >= ConsecutiveFailurePause ? $"{consecutiveFailures} moves failed in a row (last: {ex.Message})"
                    : null;
                if (why is not null)
                {
                    result.PausedForRootId = !TargetReachable(mv) ? (mv.ToRootId ?? mv.FromRootId) : !SourceReachable(mv) ? mv.FromRootId : null;
                    lock (job.SyncRoot) { mv.State = ReorgMoveState.Pending; mv.Error = null; mv.CopiedBytes = 0; }
                    result.Outcome = ReorgOutcome.Paused;
                    result.PauseReason = why;
                    SaveJournal(job);
                    await SaveManifestAsync(ct);
                    lock (job.SyncRoot) job.Current = null;
                    // Nothing of this file landed (its .part was discarded): the card's bar goes back to 0, not to full.
                    RaiseProgress(job, mv, 0);
                    return Finalize(job, result);
                }
            }

            if (mv.State == ReorgMoveState.Done) { result.Moved++; consecutiveFailures = 0; }
            else if (mv.State == ReorgMoveState.Skipped) { result.Skipped++; consecutiveFailures = 0; }
            else if (mv.State == ReorgMoveState.Failed) result.Failed++;

            SaveJournal(job);
            await SaveManifestAsync(ct);
            RaiseProgress(job, mv, 1);
        }

        lock (job.SyncRoot) job.Current = null;
        return Finalize(job, result);
    }

    // Serialize journal writes against reorder mutations of job.Moves (both touch the list).
    private void SaveJournal(ReorgJob job) { lock (job.SyncRoot) _journal.Save(job); }

    /// <summary>Puts a stopped job's already-moved files back, newest first, then clears the journal. Each
    /// file is moved back and repathed before the next is attempted (journal saved as it goes), so an
    /// interrupted revert leaves a smaller, still-valid job. Files gone from the destination count as
    /// unrecoverable and are skipped; the rest still go back.</summary>
    public async Task<ReorgRevertResult> RevertAsync(ReorgJob job, CancellationToken ct = default)
    {
        var res = new ReorgRevertResult();
        List<ReorgMove> settled;
        lock (job.SyncRoot) settled = job.Moves.Where(m => m.State == ReorgMoveState.Done).ToList();
        settled.Reverse();   // newest first: unwinding in reverse order of application

        foreach (var mv in settled)
        {
            // Stopping a revert is cooperative and between files, never mid-file; the journal survives so
            // the rest can still be put back later.
            if (ct.IsCancellationRequested) { res.Stopped = true; return res; }
            try
            {
                if (!File.Exists(mv.ToAbs)) { res.Unrecoverable++; continue; }
                var destDir = Path.GetDirectoryName(mv.FromAbs);
                if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
                if (!string.Equals(mv.FromAbs, mv.ToAbs, StringComparison.OrdinalIgnoreCase))
                    File.Move(mv.ToAbs, mv.FromAbs, overwrite: true);

                RepathBack(mv);
                mv.State = ReorgMoveState.Pending;
                mv.CopiedBytes = 0;
                PruneEmptySourceDirs(mv.ToAbs);
                res.Restored++;
            }
            catch (Exception ex)
            {
                mv.Error = ex.Message;
                res.Failed++;
            }
            SaveJournal(job);
            await SaveManifestAsync(ct);
        }

        // Nothing left to resolve, so the plan can go. (A stopped revert returned above and keeps it --
        // the plan is the only record of where the remaining files belong.)
        _journal.Delete();
        return res;
    }

    /// <summary>The inverse of <see cref="Repath"/>: points the manifest back at the original location.</summary>
    private void RepathBack(ReorgMove mv)
    {
        using (_store.Gate.Enter())   // (manifest gate 09-08)
        {
            var m = _store.Current;
            var item = m.ItemById(mv.GogId);
            var f = item?.Files.FirstOrDefault(x => x.FileKey == mv.FileKey)
                    ?? item?.OldVersionFiles.FirstOrDefault(x => x.FileKey == mv.FileKey);
            if (f is null) return;
            if (mv.FromRootId is not null) f.RootId = mv.FromRootId;
            f.LocalRelativePath = mv.FromRel;
        }
    }

    private ReorgResult Finalize(ReorgJob job, ReorgResult result)
    {
        if (result.Outcome == ReorgOutcome.Paused)
        {
            // Record the pause in the journal so it survives a restart, like the paused download queue
            // (LibraryManifest.Downloads.Paused); the transient ReorgControl dies with the process.
            lock (job.SyncRoot) job.Paused = true;
            SaveJournal(job);
            return result;   // journal kept for resume
        }

        if (result.Outcome == ReorgOutcome.Canceled)
        {
            // Keep the journal: the plan is the only record of what moved where, so it survives until the
            // user resolves it -- revert, or discard and accept the orphans.
            lock (job.SyncRoot) { job.Canceled = true; job.Paused = false; }
            DiscardParts(job);
            SaveJournal(job);
            return result;
        }

        // Reached the end of the plan.
        if (job.HasFailures)
        {
            result.Outcome = ReorgOutcome.CompletedWithErrors;
            SaveJournal(job);   // keep so failed moves can be retried
        }
        else
        {
            result.Outcome = ReorgOutcome.Completed;
            _journal.Delete();
        }
        return result;
    }

    private async Task ExecuteMoveAsync(ReorgMove mv, ReorgJob job, ReorgControl control, CancellationToken ct)
    {
        if (mv.IsSettled) return;

        var fromExists = File.Exists(mv.FromAbs);
        var toExists = File.Exists(mv.ToAbs);
        var samePath = string.Equals(Path.GetFullPath(mv.FromAbs), Path.GetFullPath(mv.ToAbs),
            StringComparison.OrdinalIgnoreCase);

        // Already landed (e.g. crash after the move, before we recorded Done): just repath + settle.
        if (toExists && (!fromExists || samePath))
        {
            Repath(mv);
            mv.State = ReorgMoveState.Done;
            return;
        }

        // Nothing on disk to move (never downloaded): still repath so the manifest matches the new scheme
        // and future writes land in the right place. NOT when the source VOLUME is gone: that file may well
        // exist, and repathing it would point the manifest at a place it never went (then read as Missing).
        if (!fromExists)
        {
            if (!SourceReachable(mv))
                throw new IOException($"The drive holding '{mv.Title}' ({mv.FromRel}) is not reachable.");
            Repath(mv);
            mv.State = ReorgMoveState.Skipped;
            return;
        }
        if (!TargetReachable(mv))
            throw new IOException($"The destination drive for '{mv.Title}' is not reachable.");

        Directory.CreateDirectory(Path.GetDirectoryName(mv.ToAbs)!);

        if (mv.Kind == ReorgMoveKind.Rename)
        {
            // Same volume: atomic rename. Nothing is copied, so nothing needs hash-verifying.
            if (toExists) File.Delete(mv.ToAbs);   // stale target from a prior partial run
            File.Move(mv.FromAbs, mv.ToAbs);
            Repath(mv);
            mv.State = ReorgMoveState.Done;
            PruneEmptySourceDirs(mv.FromAbs);
            return;
        }

        // Cross-drive: copy -> verify -> promote -> delete source. Never delete-first.
        if (mv.State != ReorgMoveState.Verified)
        {
            var part = mv.ToAbs + PartSuffix;
            if (File.Exists(part)) File.Delete(part);   // discard any partial from an earlier attempt

            // Any failure or interruption mid-copy (IO error, pause, cancel) takes its .grogpart with it: nothing
            // sweeps parts later, and a half-written part is worth nothing since the next attempt restarts.
            try
            {
                await CopyWithProgressAsync(mv, part, job, control, ct);
            }
            catch
            {
                DiscardPart(mv, job);
                throw;
            }
            mv.State = ReorgMoveState.Copied;
            SaveJournal(job);

            if (!await VerifyCopyAsync(mv, part, ct))
            {
                DiscardPart(mv, job);
                throw new InvalidDataException(
                    $"Verification failed after copying '{mv.Title}' ({mv.FromRel}). Source left untouched.");
            }

            if (File.Exists(mv.ToAbs)) File.Delete(mv.ToAbs);
            File.Move(part, mv.ToAbs);
            mv.State = ReorgMoveState.Verified;
            SaveJournal(job);
        }

        // State == Verified: destination is present + verified. Point the manifest at it, then and only
        // then remove the source.
        Repath(mv);
        if (File.Exists(mv.FromAbs) && !samePath) File.Delete(mv.FromAbs);
        lock (job.SyncRoot) { mv.State = ReorgMoveState.Done; mv.CopiedBytes = 0; }
        // Same tidy-up the rename path does: an emptied game folder on the source drive is ours to remove
        // (owner 09-04: a drive emptied by a cross-drive move kept every Games/<slug>/ shell).
        if (!samePath) PruneEmptySourceDirs(mv.FromAbs);
        PruneEmptySourceDirs(mv.FromAbs);
    }

    /// <summary>Removes one move's un-promoted .grogpart (best-effort) and forgets its copied bytes.</summary>
    private static void DiscardPart(ReorgMove mv, ReorgJob job)
    {
        try { var part = mv.ToAbs + PartSuffix; if (File.Exists(part)) File.Delete(part); } catch { /* best-effort */ }
        lock (job.SyncRoot) mv.CopiedBytes = 0;
    }

    /// <summary>Sweeps the .grogpart of every move that has not landed (Pending/Copied/Failed). Run when a job
    /// is canceled or discarded so a stopped reorg leaves no partial files on the destination drive.</summary>
    public static void DiscardParts(ReorgJob job)
    {
        List<ReorgMove> open;
        lock (job.SyncRoot)
            open = job.Moves.Where(x => x.Kind == ReorgMoveKind.CopyVerifyDelete
                                     && x.State is ReorgMoveState.Pending or ReorgMoveState.Copied or ReorgMoveState.Failed).ToList();
        foreach (var mv in open) DiscardPart(mv, job);
    }

    /// <summary>The emptied source chain goes (owner-hit 2026-08-30: a layout reorg left every old game/bucket
    /// folder behind as an empty husk). One rule, shared with Delete: <see cref="FolderPruner"/>.</summary>
    private static void PruneEmptySourceDirs(string fromAbs) => FolderPruner.PruneEmptyChainAbove(fromAbs);

    private async Task CopyWithProgressAsync(ReorgMove mv, string destPart, ReorgJob job, ReorgControl control, CancellationToken ct)
    {
        const int bufferSize = 1024 * 1024;   // 1 MB
        var buffer = new byte[bufferSize];
        long copied = 0;
        var total = mv.SizeBytes > 0 ? mv.SizeBytes : (File.Exists(mv.FromAbs) ? new FileInfo(mv.FromAbs).Length : 0);

        await using var src = new FileStream(mv.FromAbs, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, useAsync: true);
        await using var dst = new FileStream(destPart, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, useAsync: true);

        int read;
        while ((read = await src.ReadAsync(buffer.AsMemory(0, bufferSize), ct)) > 0)
        {
            // Honor Pause/Cancel per chunk, not just between files: the caller's catch treats this as a
            // pause/cancel, the un-promoted .grogpart is discarded on the next attempt, source untouched.
            if (control.IsPaused || control.IsCanceled) throw new OperationCanceledException();
            await dst.WriteAsync(buffer.AsMemory(0, read), ct);
            copied += read;
            // Once per chunk (1 MB): what is already on the target so RemainingWrites() stops reserving it twice.
            lock (job.SyncRoot) mv.CopiedBytes = copied;
            var frac = total > 0 ? Math.Clamp((double)copied / total, 0, 1) : 0;
            RaiseProgress(job, mv, frac);
        }

        // Cross-drive: the bytes must be on the platter before verify passes and the source is deleted.
        await dst.FlushAsync(ct);
        dst.Flush(flushToDisk: true);
    }

    private static async Task<bool> VerifyCopyAsync(ReorgMove mv, string destPart, CancellationToken ct)
    {
        if (!File.Exists(destPart)) return false;

        // Prefer the checksum GOG published; fall back to size when there is no hash.
        if (!string.IsNullOrEmpty(mv.ExpectedMd5))
            return string.Equals(await Hashing.Md5Async(destPart, ct), mv.ExpectedMd5, StringComparison.OrdinalIgnoreCase);

        // No hash: a real recorded size is the yardstick. Only a rounded GOG label (SizeIsLabel, off by a few
        // KB, which would fail every good copy) defers to the source file's real length.
        var destLen = new FileInfo(destPart).Length;
        if (!mv.SizeIsLabel && mv.SizeBytes > 0) return destLen == mv.SizeBytes;
        if (File.Exists(mv.FromAbs)) return destLen == new FileInfo(mv.FromAbs).Length;
        return mv.SizeBytes > 0 && destLen == mv.SizeBytes;
    }

    private void Repath(ReorgMove mv)
    {
        using (_store.Gate.Enter())   // (manifest gate 09-08)
        {
            var m = _store.Current;
            var item = m.ItemById(mv.GogId);
            // Search OldVersionFiles too: a relocated Old Versions/ entry lives there, not in Files.
            var f = item?.Files.FirstOrDefault(x => x.FileKey == mv.FileKey)
                    ?? item?.OldVersionFiles.FirstOrDefault(x => x.FileKey == mv.FileKey);
            if (f is null) return;
            if (mv.ToRootId is not null) f.RootId = mv.ToRootId;
            f.LocalRelativePath = mv.ToRel;
        }
    }


    private async Task SaveManifestAsync(CancellationToken ct)
    {
        try { await _store.SaveAsync(ct); }
        catch { /* journal is the durable record; a failed manifest save is recovered on resume */ }
    }

    private void RaiseProgress(ReorgJob job, ReorgMove current, double fileFraction)
    {
        if (Progress is null) return;
        long copied = (long)(current.SizeBytes * Math.Clamp(fileFraction, 0, 1));
        long bytesDone = job.BytesSettled + copied;
        Progress.Invoke(new ReorgProgress(
            FilesDone: job.Settled,
            FilesTotal: job.Total,
            BytesDone: bytesDone,
            BytesTotal: job.BytesTotal,
            CurrentTitle: current.Title,
            CurrentFromRel: current.FromRel,
            CurrentToRel: current.ToRel,
            CurrentFileFraction: Math.Clamp(fileFraction, 0, 1),
            CurrentCopiedBytes: copied,
            CurrentSizeBytes: current.SizeBytes,
            CurrentToRootId: current.ToRootId ?? current.FromRootId));
    }
}
