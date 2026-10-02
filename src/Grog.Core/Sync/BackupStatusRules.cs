// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using Grog.Core.Models;

namespace Grog.Core.Sync;

/// <summary>
/// What a status MEANS for action, in one place. Consumers use these predicates rather than hand-listing
/// statuses, so adding a status forces one decision here, not a hunt through call sites.
/// </summary>
public static class BackupStatusRules
{
    /// <summary>There is backup work outstanding for this product: something is absent, broken, or stale.
    /// Downloading is excluded on purpose -- it's already in flight, not waiting to be started.</summary>
    public static bool NeedsWork(this BackupStatus s) => s switch
    {
        BackupStatus.NotBackedUp => true,     // never fetched
        BackupStatus.Partial => true,           // some files present
        BackupStatus.Missing => true,           // was backed up, gone from disk
        BackupStatus.Corrupt => true,             // corrupt, needs re-fetch
        BackupStatus.Outdated => true,   // present but stale
        BackupStatus.Complete => false,
        BackupStatus.Unknown => false,          // nothing downloadable
        _ => false,
    };

    /// <summary>Work outstanding that ISN'T just an update -- the "not backed up yet" bucket, kept apart
    /// so the dashboard can say "3 missing, 2 updates" rather than lumping them.</summary>
    public static bool NeedsFirstBackup(this BackupStatus s) => s.NeedsWork() && s != BackupStatus.Outdated;

    /// <summary>The product's files are all present and accounted for.</summary>
    public static bool IsSettled(this BackupStatus s) => s == BackupStatus.Complete;
}
