// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System.Linq;
using Grog.Core.Manifest;
using Grog.Core.Models;

namespace Grog.Core.Sync;

/// <summary>Manual truth-telling about local backup state. When a drive is lost/stolen or a file is
/// corrupted or deleted outside Grog, there's nothing on disk to scan, so the user tells Grog directly.
/// This only flips manifest state (never touches disk) and keeps each file's recorded location, so a
/// later re-download puts it back where it was. (Losing a whole device is handled by removing the
/// device, which forgets locations -- a different, deliberate action.)</summary>
public static class ManualBackupState
{
    /// <summary>Force every file of a product to a given state -- a manual flag assertion (no disk touch).
    /// Used by the right-click "Mark as Not Backed Up" (NotDownloaded) and "Mark as Missing"
    /// (MissingLocally) actions. Location (RootId + relative path) is kept. Returns files changed.</summary>
    public static int MarkProduct(LibraryManifest m, long gogId, FileState target)
    {
        var item = m.ItemById(gogId);
        if (item is null) return 0;
        int n = 0;
        foreach (var f in item.Files)
            if (f.State != target && Applies(f, target)) { f.State = target; n++; }
        return n;
    }

    /// <summary>May this file take the asserted state? "Missing" means bytes we HELD are gone, so it applies only
    /// to a file that held some (current, superseded or corrupt): stamped on a never-downloaded file it painted a
    /// red "missing" on something that was never there and sent it through Fix It instead of the normal queue
    /// (sweep 2 #26). Unavailable is GOG's fact about the file, not ours about a copy: no manual flag replaces it.</summary>
    public static bool Applies(GameFile f, FileState target)
    {
        if (f.State == FileState.Unavailable) return false;
        if (target == FileState.Missing) return BackupScope.HasLocalCopy(f) || f.State == FileState.Corrupt;
        return true;
    }

    /// <summary>Force a single file to a given state. Returns 1 if it changed, else 0.</summary>
    public static int MarkFile(LibraryManifest m, long gogId, string fileKey, FileState target)
    {
        var f = m.ItemById(gogId)?.Files.FirstOrDefault(x => x.FileKey == fileKey);
        if (f is null || f.State == target || !Applies(f, target)) return 0;
        f.State = target;
        return 1;
    }
}
