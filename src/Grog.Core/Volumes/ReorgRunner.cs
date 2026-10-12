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

public sealed class ReorgRunner
{
    private const string PartSuffix = ".grogpart";

    private readonly IManifestStore _store;
    private readonly ReorgJournalStore _journal;

    public event Action<ReorgProgress>? Progress;

    public ReorgRunner(IManifestStore store, ReorgJournalStore journal)
    {
        _store = store;
        _journal = journal;
    }

    public ReorgJob? LoadPending() => _journal.Load();

    public Func<string, bool> VolumeReachable { get; set; } = path =>
    {
        try
        {
            // The storage folder itself, else its nearest existing ancestor
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
    private bool Reachable(string abs, string rel)
    {
        var (folder, exact) = StorageFolder(abs, rel);
        return (!exact || Directory.Exists(folder)) && VolumeReachable(folder);
    }
    internal bool SourceReachableForTest(ReorgMove mv) => SourceReachable(mv);
    internal bool TargetReachableForTest(ReorgMove mv) => TargetReachable(mv);
    private bool SourceReachable(ReorgMove mv) => Reachable(mv.FromAbs, mv.FromRel);
    private bool TargetReachable(ReorgMove mv) => Reachable(mv.ToAbs, mv.ToRel);

    public const int ConsecutiveFailurePause = 3;

    public async Task<ReorgResult> RunAsync(ReorgJob job, ReorgControl control, CancellationToken ct = default)
    {
        var m = _store.Current;
        lock (job.SyncRoot)
        {
            job.Paused = false;
            foreach (var x in job.Moves)
                if (x.State == ReorgMoveState.Failed) { x.State = ReorgMoveState.Pending; x.Error = null; }
        }
        SaveJournal(job);   // ensure the plan is on disk before touching a single file

        var result = new ReorgResult();
        int consecutiveFailures = 0;

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

    public async Task<ReorgRevertResult> RevertAsync(ReorgJob job, CancellationToken ct = default)
    {
        var res = new ReorgRevertResult();
        List<ReorgMove> settled;
        lock (job.SyncRoot) settled = job.Moves.Where(m => m.State == ReorgMoveState.Done).ToList();
        settled.Reverse();   // newest first: unwinding in reverse order of application

        foreach (var mv in settled)
        {
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

        _journal.Delete();
        return res;
    }

    private void RepathBack(ReorgMove mv)
    {
        using (_store.Gate.Enter())   // 
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
            lock (job.SyncRoot) job.Paused = true;
            SaveJournal(job);
            return result;   // journal kept for resume
        }

        if (result.Outcome == ReorgOutcome.Canceled)
        {
            lock (job.SyncRoot) { job.Canceled = true; job.Paused = false; }
            DiscardParts(job);
            SaveJournal(job);
            return result;
        }

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
            if (toExists) File.Delete(mv.ToAbs);   // stale target from a prior partial run
            File.Move(mv.FromAbs, mv.ToAbs);
            Repath(mv);
            mv.State = ReorgMoveState.Done;
            PruneEmptySourceDirs(mv.FromAbs);
            return;
        }

        // Cross-drive
        if (mv.State != ReorgMoveState.Verified)
        {
            var part = mv.ToAbs + PartSuffix;
            if (File.Exists(part)) File.Delete(part);   // discard any partial from an earlier attempt

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

        // State == Verified
        Repath(mv);
        if (File.Exists(mv.FromAbs) && !samePath) File.Delete(mv.FromAbs);
        lock (job.SyncRoot) { mv.State = ReorgMoveState.Done; mv.CopiedBytes = 0; }
        if (!samePath) PruneEmptySourceDirs(mv.FromAbs);
        PruneEmptySourceDirs(mv.FromAbs);
    }

    private static void DiscardPart(ReorgMove mv, ReorgJob job)
    {
        try { var part = mv.ToAbs + PartSuffix; if (File.Exists(part)) File.Delete(part); } catch { /* best-effort */ }
        lock (job.SyncRoot) mv.CopiedBytes = 0;
    }

    public static void DiscardParts(ReorgJob job)
    {
        List<ReorgMove> open;
        lock (job.SyncRoot)
            open = job.Moves.Where(x => x.Kind == ReorgMoveKind.CopyVerifyDelete
                                     && x.State is ReorgMoveState.Pending or ReorgMoveState.Copied or ReorgMoveState.Failed).ToList();
        foreach (var mv in open) DiscardPart(mv, job);
    }

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
            if (control.IsPaused || control.IsCanceled) throw new OperationCanceledException();
            await dst.WriteAsync(buffer.AsMemory(0, read), ct);
            copied += read;
            lock (job.SyncRoot) mv.CopiedBytes = copied;
            var frac = total > 0 ? Math.Clamp((double)copied / total, 0, 1) : 0;
            RaiseProgress(job, mv, frac);
        }

        // Cross-drive
        await dst.FlushAsync(ct);
        dst.Flush(flushToDisk: true);
    }

    private static async Task<bool> VerifyCopyAsync(ReorgMove mv, string destPart, CancellationToken ct)
    {
        if (!File.Exists(destPart)) return false;

        // Prefer the checksum GOG published; fall back to size when there is no hash.
        if (!string.IsNullOrEmpty(mv.ExpectedMd5))
            return string.Equals(await Hashing.Md5Async(destPart, ct), mv.ExpectedMd5, StringComparison.OrdinalIgnoreCase);

        // No hash
        var destLen = new FileInfo(destPart).Length;
        if (!mv.SizeIsLabel && mv.SizeBytes > 0) return destLen == mv.SizeBytes;
        if (File.Exists(mv.FromAbs)) return destLen == new FileInfo(mv.FromAbs).Length;
        return mv.SizeBytes > 0 && destLen == mv.SizeBytes;
    }

    private void Repath(ReorgMove mv)
    {
        using (_store.Gate.Enter())   // 
        {
            var m = _store.Current;
            var item = m.ItemById(mv.GogId);
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
