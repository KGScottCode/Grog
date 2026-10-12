// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System.Linq;
using Grog.Core.Manifest;
using Grog.Core.Models;

namespace Grog.Core.Sync;

public static class ManualBackupState
{
    public static int MarkProduct(LibraryManifest m, long gogId, FileState target)
    {
        var item = m.ItemById(gogId);
        if (item is null) return 0;
        int n = 0;
        foreach (var f in item.Files)
            if (f.State != target && Applies(f, target)) { f.State = target; n++; }
        return n;
    }

    public static bool Applies(GameFile f, FileState target)
    {
        if (f.State == FileState.Unavailable) return false;
        if (target == FileState.Missing) return BackupScope.HasLocalCopy(f) || f.State == FileState.Corrupt;
        return true;
    }

    public static int MarkFile(LibraryManifest m, long gogId, string fileKey, FileState target)
    {
        var f = m.ItemById(gogId)?.Files.FirstOrDefault(x => x.FileKey == fileKey);
        if (f is null || f.State == target || !Applies(f, target)) return 0;
        f.State = target;
        return 1;
    }
}
