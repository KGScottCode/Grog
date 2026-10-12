// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Sync;

namespace Grog.Core.Download;

public static class DownloadSettlement
{
    public enum Outcome { Completed, Unavailable, Skipped, WillRetry, GaveUp, NotFinished, NoRoom, DriveOffline }

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
                if (file is not null)
                {
                    file.State = FileState.Unavailable;
                    file.UnavailableReason = refusal;
                    file.FailedAttempts = 0;
                    file.HasPartial = false; file.PartialBytes = null;
                }
                return (Outcome.Unavailable, manifest.Downloads.Remove(gogId, key));

            case DownloadTaskState.Skipped:
                return (Outcome.Skipped, manifest.Downloads.Remove(gogId, key));

            case DownloadTaskState.Failed when t.DriveGone:
                return (Outcome.DriveOffline, false);

            case DownloadTaskState.Failed when t.DiskFull:
                manifest.Downloads.SetPlacement(gogId, key, null, false);
                return (Outcome.NoRoom, true);

            case DownloadTaskState.Failed when file is not null && Sync.BackupScope.IsPresent(file):
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

    public static void RequeueFresh(LibraryManifest manifest, long gogId, GameFile f)
    {
        bool condemned = f.State == FileState.Corrupt;
        RetryPolicy.ManualRetry(f);
        f.State = FileState.NotBackedUp;
        if (condemned) { f.HasPartial = false; f.PartialBytes = null; }
        f.RefetchRequested = true;   // the bytes on disk are the ones being replaced: never adopt them by size
        manifest.Downloads.Enqueue(gogId, f.FileKey);
    }
}
