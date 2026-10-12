// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using Grog.Core.Models;

namespace Grog.Core.Sync;

public static class BackupStatusRules
{
    public static bool NeedsWork(this BackupStatus s) => s switch
    {
        BackupStatus.NotBackedUp => true,   // never fetched
        BackupStatus.Partial => true,       // some files present
        BackupStatus.Missing => true,       // was backed up, gone from disk
        BackupStatus.Corrupt => true,       // corrupt, needs re-fetch
        BackupStatus.Outdated => true,      // present but stale
        BackupStatus.Complete => false,
        BackupStatus.Unknown => false,      // nothing downloadable
        _ => false,
    };

    public static bool NeedsFirstBackup(this BackupStatus s) => s.NeedsWork() && s != BackupStatus.Outdated;

    public static bool IsSettled(this BackupStatus s) => s == BackupStatus.Complete;
}
