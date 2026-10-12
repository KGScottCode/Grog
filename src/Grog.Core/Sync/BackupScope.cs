using System.Collections.Generic;
using System.Linq;
using Grog.Core.Models;

namespace Grog.Core.Sync;

public static class BackupScope
{

    public static bool CountsTowardCompleteness(GameFile f) => f.State != FileState.Unavailable;

    public static IEnumerable<GameFile> Scoped(LibraryItem item, Scope scope)
        => item.Files.Where(f => scope.Includes(f) && CountsTowardCompleteness(f));


    public static bool IsPresent(GameFile f) => IsPresent(f.State);
    public static bool IsPresent(FileState s) => s is FileState.Present or FileState.Verified;

    public static bool HasLocalCopy(GameFile f)
        => f.State is FileState.Present or FileState.Verified or FileState.Outdated;

    public static bool HoldsBytes(GameFile f)
        => !string.IsNullOrEmpty(f.LocalRelativePath)
           && f.State is FileState.Present or FileState.Verified or FileState.Outdated or FileState.Corrupt or FileState.Missing;

    public static bool IsWithdrawnOnDisk(GameFile f)
        => f.WithdrawnByGog && HoldsBytes(f) && f.State != FileState.Missing;

    public static bool NeedsFetch(GameFile f) => NeedsFetch(f.State);
    public static bool NeedsFetch(FileState s) => s is FileState.NotBackedUp or FileState.Outdated or FileState.Missing;

    public static BackupStatus Status(LibraryItem item, Scope scope)
    {
        var scoped = Scoped(item, scope).ToList();
        if (scoped.Count == 0) return BackupStatus.Complete;   // nothing in scope -> nothing to do
        return StatusOf(scoped);
    }

    public static BackupStatus StatusOf(IReadOnlyList<GameFile> files)
    {
        if (files.Any(f => f.State == FileState.Corrupt)) return BackupStatus.Corrupt;
        if (files.Any(f => f.State == FileState.Missing)) return BackupStatus.Missing;
        int present = files.Count(IsPresent);
        if (present == 0) return BackupStatus.NotBackedUp;
        if (files.Any(f => f.State == FileState.Outdated)) return BackupStatus.Outdated;
        return present < files.Count ? BackupStatus.Partial : BackupStatus.Complete;
    }

    public static bool IsComplete(LibraryItem item, Scope scope) => Status(item, scope) == BackupStatus.Complete;

    public static long ScopedTotalBytes(LibraryItem item, Scope scope)
        => Scoped(item, scope).Sum(f => Rollups.TotalBytesOf(f));

    public static long ScopedDoneBytes(LibraryItem item, Scope scope)
        => Scoped(item, scope).Where(IsPresent).Sum(f => Rollups.TotalBytesOf(f));

    
}
