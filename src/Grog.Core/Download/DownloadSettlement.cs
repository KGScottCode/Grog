// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Sync;

namespace Grog.Core.Download;

/// <summary>
/// What a finished task means for the manifest RECORD: the one place the retry strikes, the Unavailable
/// mark, the partial flags and the persisted queue entry are settled. The App's settle path was the only
/// copy until 09-02; the CLI's download verb settled nothing, so a file GOG refused was never marked
/// Unavailable and a repeatedly failing one was never condemned when run from a script.
/// </summary>
public static class DownloadSettlement
{
    public enum Outcome { Completed, Unavailable, Skipped, WillRetry, GaveUp, NotFinished, NoRoom, DriveOffline }

    /// <summary>Apply the task's terminal state to <paramref name="file"/> (the manifest's own record for
    /// the task's file; null when the manifest no longer tracks it) and the persisted queue. Returns what
    /// happened and whether the manifest changed. Canceled tasks are left alone: the queue entry stays
    /// so a resume picks the file up, and the partial is the engine's to keep or drop.</summary>
    public static (Outcome Outcome, bool Changed) Settle(LibraryManifest manifest, DownloadTask t, GameFile? file)
    {
        long gogId = t.File.GameGogId; string key = t.File.FileKey;
        switch (t.State)
        {
            case DownloadTaskState.Completed:
                if (file is not null)
                {
                    RetryPolicy.RecordSuccess(file);
                    file.PartialBytes = null; file.HasPartial = false;   // complete: the partial is now the full file
                }
                return (Outcome.Completed, manifest.Downloads.Remove(gogId, key));

            case DownloadTaskState.Failed when t.UnavailableReason is { } refusal:
                // GOG refuses to serve this file: no strike, never Corrupt -- leaves the queue as Unavailable
                // carrying GOG's own words. Sync clears it if the entitlement changes.
                if (file is not null)
                {
                    file.State = FileState.Unavailable;
                    file.UnavailableReason = refusal;
                    file.FailedAttempts = 0;
                    file.HasPartial = false; file.PartialBytes = null;
                }
                return (Outcome.Unavailable, manifest.Downloads.Remove(gogId, key));

            case DownloadTaskState.Skipped:
                // Every owner signed out: not a failure, no strike, state untouched. Leaves the queue for
                // this run and re-queues like any other gap once an owner signs back in.
                return (Outcome.Skipped, manifest.Downloads.Remove(gogId, key));

            case DownloadTaskState.Failed when t.DriveGone:
                // (09-25) Its storage went away: the file did nothing wrong. No strike, the entry and its partial stay
                // exactly as they are; it resumes when the drive is back (or goes where the user sends it).
                return (Outcome.DriveOffline, false);

            case DownloadTaskState.Failed when t.DiskFull:
                // The DRIVE refused the bytes; the file did nothing wrong. No strike (three full-disk runs condemned
                // intact files as Corrupt and dropped them from the queue, QA 09-19). It stays queued with its
                // partial, flagged won't-fit until a re-plan finds it room.
                manifest.Downloads.SetPlacement(gogId, key, null, false);
                return (Outcome.NoRoom, true);

            case DownloadTaskState.Failed when file is not null && Sync.BackupScope.IsPresent(file):
                // (09-26) The file already landed (a duplicate attempt, or a late failure after the success settled):
                // no strike on a good file, and the partial flag the failed attempt raised is dropped. grog.log 09-25
                // left Verified files carrying strikes and HasPartial.
                if (file.HasPartial) { file.HasPartial = false; file.PartialBytes = null; return (Outcome.NotFinished, true); }
                return (Outcome.NotFinished, false);

            case DownloadTaskState.Failed:
                var decision = file is not null ? RetryPolicy.RecordFailure(file) : RetryDecision.GiveUp;
                return decision == RetryDecision.GiveUp
                    ? (Outcome.GaveUp, manifest.Downloads.Remove(gogId, key))
                    : (Outcome.WillRetry, file is not null);   // the strike itself is a manifest change

            default:
                return (Outcome.NotFinished, false);
        }
    }

    /// <summary>The user asked for a fresh copy: strikes cleared, Corrupt lifted, partial forgotten, state
    /// back to NotBackedUp so the normal path fetches from scratch (never resume condemned bytes).
    /// Three App sites spelled this out before 09-02.</summary>
    public static void RequeueFresh(LibraryManifest manifest, long gogId, GameFile f)
    {
        // Only CONDEMNED bytes are forgotten. A file that merely failed (network, disk full) keeps its resumable
        // partial: clearing the record left the .part on the drive with no owner, so Try Again after a disk-full
        // run orphaned a 4 GiB partial that then held the drive at 0 B free (walk 09-19).
        bool condemned = f.State == FileState.Corrupt;
        RetryPolicy.ManualRetry(f);
        f.State = FileState.NotBackedUp;
        if (condemned) { f.HasPartial = false; f.PartialBytes = null; }
        f.RefetchRequested = true;   // the bytes on disk are the ones being replaced: never adopt them by size
        manifest.Downloads.Enqueue(gogId, f.FileKey);
    }
}
