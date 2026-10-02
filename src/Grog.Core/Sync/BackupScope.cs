using System.Collections.Generic;
using System.Linq;
using Grog.Core.Models;

namespace Grog.Core.Sync;

/// <summary>
/// The single definition of "backed up": a product's BackupStatus is always DERIVED from its files via a
/// full <see cref="Scope"/>, never stored independently. CANON: exactly TWO present-rules, no third --
/// <see cref="IsPresent"/> (drives every completeness/gap/status/queue number; a superseded build is NOT
/// present) and <see cref="HasLocalCopy"/> (bytes in some form, for the scope-INDEPENDENT disk view only).
/// </summary>
public static class BackupScope
{

    /// <summary>The Unavailable rule, named once: a file GOG refuses to serve is not a gap the user can
    /// close, so it never enters a completeness number (same reasoning as Old Versions - you cannot be
    /// incomplete against something you were never offered).</summary>
    public static bool CountsTowardCompleteness(GameFile f) => f.State != FileState.Unavailable;

    public static IEnumerable<GameFile> Scoped(LibraryItem item, Scope scope)
        => item.Files.Where(f => scope.Includes(f) && CountsTowardCompleteness(f));


    /// <summary>Present on disk AS GOG CURRENTLY SHIPS IT, independent of scope. Outdated is deliberately
    /// NOT present: a superseded build is a file you do not have, so it joins the gap and queues like any
    /// other; downloading it replaces the superseded copy.</summary>
    public static bool IsPresent(GameFile f) => IsPresent(f.State);
    /// <summary>The same rule for callers that hold a bare state (detail rows, sweep transitions).</summary>
    public static bool IsPresent(FileState s) => s is FileState.Present or FileState.Verified;

    /// <summary>On disk in SOME form, current or superseded. Used where the question is "do we hold bytes for
    /// this file" (composition, orphan/import matching) rather than "is our copy the one GOG ships".</summary>
    public static bool HasLocalCopy(GameFile f)
        => f.State is FileState.Present or FileState.Verified or FileState.Outdated;

    /// <summary>We hold a RECORD of bytes on disk for this file, whatever their health: current, superseded,
    /// failed its hash (Corrupt), or last seen gone (Missing, still worth clearing). The one rule for "is there
    /// something of ours to delete", used by Delete's preview and plan. NOT a present-rule: <see cref="IsPresent"/>
    /// and <see cref="HasLocalCopy"/> answer "backed up"; this answers "do we point at bytes". Moved here from
    /// LocalDeletion 09-13 so no call site re-derives it.</summary>
    public static bool HoldsBytes(GameFile f)
        => !string.IsNullOrEmpty(f.LocalRelativePath)
           && f.State is FileState.Present or FileState.Verified or FileState.Outdated or FileState.Corrupt or FileState.Missing;

    /// <summary>An archive-list record for a slot GOG withdrew whose bytes are still on disk: the Library lists
    /// it under "On disk, outside scope" and Delete reaches it.</summary>
    public static bool IsWithdrawnOnDisk(GameFile f)
        => f.WithdrawnByGog && HoldsBytes(f) && f.State != FileState.Missing;

    /// <summary>A gap the queue can close: never fetched, superseded, or gone from disk. Corrupt is NOT a
    /// gap here (it waits for a retry decision) and Unavailable never is. Six call sites re-derived this
    /// before 09-02.</summary>
    public static bool NeedsFetch(GameFile f) => NeedsFetch(f.State);
    public static bool NeedsFetch(FileState s) => s is FileState.NotBackedUp or FileState.Outdated or FileState.Missing;

    /// <summary>The traffic-light status for a product measured against the given scope.</summary>
    public static BackupStatus Status(LibraryItem item, Scope scope)
    {
        var scoped = Scoped(item, scope).ToList();
        if (scoped.Count == 0) return BackupStatus.Complete;   // nothing in scope -> nothing to do
        return StatusOf(scoped);
    }

    /// <summary>The rollup over an already-filtered file list; the ONE implementation shared by the scoped
    /// path above and LibrarySyncService.RecomputeStatus. The caller decides what an empty list means
    /// (Complete for a scope with nothing in it, Unknown for a product with no files).</summary>
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

    /// <summary>Total in-scope bytes for a product (the denominator for byte-weighted progress).</summary>
    public static long ScopedTotalBytes(LibraryItem item, Scope scope)
        => Scoped(item, scope).Sum(f => Rollups.TotalBytesOf(f));

    /// <summary>Downloaded in-scope bytes for a product (the numerator for byte-weighted progress).</summary>
    public static long ScopedDoneBytes(LibraryItem item, Scope scope)
        => Scoped(item, scope).Where(IsPresent).Sum(f => Rollups.TotalBytesOf(f));

    // No games/extras-only helpers by design: every caller builds and passes a full Scope.
}
