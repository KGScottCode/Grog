// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;

namespace Grog.Core.Sync;

/// <summary>
/// Picks the Status page headline: the most newsworthy TRUE thing leads, and the lede stands alone -- a
/// reader who sees only this line has the whole story. A PURE function over a small snapshot so the
/// ranking is testable and lives in one place.
/// </summary>
public static class StatusLede
{
    /// <summary>Everything the ranking needs. Counts are library-wide (not this run's queue).</summary>
    public readonly record struct Snapshot(
        int OfflineFolders,      // folders that should be present and aren't
        int MissingFiles,        // backed up once, no longer on disk (NOT checksum failures)
        long UnprotectedBytes,   // bytes of in-scope files we do not hold
        int UnprotectedFiles,
        int TotalFiles,
        int ProtectedFiles,
        long ProtectedBytes,
        int OutdatedGames,       // GOG ships a newer version than our copy, AS OF the last catalog check
        TimeSpan? SinceCatalogCheck,   // null when we have never checked
        TimeSpan? StaleAfter,          // how old a check may get before we stop trusting the count
        int FailedLastRun = 0,         // files the last backup run could not fetch (from the run journal)
        string? FailedReason = null);  // why, in GOG's or the OS's words -- e.g. "There is not enough space"

    // NotStarted is SEPARATE from AllClear: with an empty catalog nothing is missing because nothing is
    // KNOWN, and only AllClear means the user is actually protected.
    public enum Kind { AllClear, NotStarted, Gap, Stale, Outdated, MissingFiles, FolderOffline, RunFailed }

    /// <summary>The chosen headline: a big value, the sentence it completes, and a supporting line.</summary>
    public readonly record struct Lede(Kind Kind, string Value, string Headline, string Detail);


    /// <summary>The one grading. FAULT is deliberately narrow - it is the same line the Folders page draws, where
    /// a fixed drive that should be there and isn't goes red while an unplugged removable does not.</summary>
    public static Severity SeverityOf(Kind k) => k switch
    {
        Kind.FolderOffline or Kind.MissingFiles => Severity.Fault,
        Kind.AllClear or Kind.NotStarted        => Severity.Quiet,
        _                                       => Severity.Attention,
    };

    /// <summary>True when the catalog check is old enough that counts derived from it should not be asserted.</summary>
    public static bool IsStale(TimeSpan? since, TimeSpan? staleAfter)
        => since is { } s && staleAfter is { } a && s > a;

    /// <summary>
    /// Ranking, worst first. A folder that should be present being gone outranks everything, because nothing
    /// else on the page can be trusted while it is true. Files that vanished outrank the gap, because losing
    /// something you had is worse news than not having fetched it yet.
    /// </summary>
    public static Lede Pick(Snapshot s)
    {
        if (s.OfflineFolders > 0)
            return new(Kind.FolderOffline,
                s.OfflineFolders == 1 ? "1 storage location" : $"{s.OfflineFolders} storage locations",
                s.OfflineFolders == 1 ? "that should be connected isn't there"
                                      : "that should be connected aren't there",
                "Your backup can't be checked or added to until it's reconnected.");

        if (s.MissingFiles > 0)
            return new(Kind.MissingFiles,
                s.MissingFiles.ToString(),
                s.MissingFiles == 1 ? "file you backed up is gone from disk"
                                    : "files you backed up are gone from disk",
                "They're still in the manifest but no longer on the drive.");

        // A failed run outranks the plain gap it leaves (it says WHY), but sits below MissingFiles:
        // losing a file you had is worse than failing to fetch one you never held.
        if (s.FailedLastRun > 0)
            return new(Kind.RunFailed,
                s.FailedLastRun.ToString(),
                s.FailedLastRun == 1 ? "file couldn't be backed up" : "files couldn't be backed up",
                string.IsNullOrWhiteSpace(s.FailedReason)
                    ? "The last backup run didn't finish. Try again -- anything already downloaded is kept."
                    : s.FailedReason!.TrimEnd('.') + ".");

        if (s.UnprotectedFiles > 0)
            return new(Kind.Gap,
                Format.ByteFormat.Size(s.UnprotectedBytes),
                "to back up",
                // No detail line: the completeness bars and the value above already carry the numbers.
                "");

        // Everything in scope is held. The only remaining question is whether we still believe that.
        if (IsStale(s.SinceCatalogCheck, s.StaleAfter))
            return new(Kind.Stale,
                Describe(s.SinceCatalogCheck!.Value),
                "since you last checked GOG for changes",
                s.OutdatedGames > 0
                    ? $"{s.OutdatedGames} game{(s.OutdatedGames == 1 ? " was" : "s were")} out of date at that point. Rescan to be sure."
                    : "Everything matched then. Rescan to be sure it still does.");

        if (s.OutdatedGames > 0)
            return new(Kind.Outdated,
                s.OutdatedGames.ToString(),
                s.OutdatedGames == 1 ? "game has a newer version on GOG"
                                     : "games have newer versions on GOG",
                s.SinceCatalogCheck is { } c
                    ? $"You hold every file, but these are older than what GOG ships now. Checked {Describe(c)} ago."
                    : "You hold every file, but these are older than what GOG ships now.");

        if (s.TotalFiles == 0)
            return new(Kind.NotStarted, "Nothing yet", "to back up - scan your GOG library to begin", "");

        return new(Kind.AllClear,
            $"All {s.TotalFiles:N0}",
            // No "fully protected" suffix: "All N files are backed up" already is that claim.
            "files are backed up",
            "");
    }

    /// <summary>The value half of a staleness headline; forwards to the one relative-time vocabulary.</summary>
    public static string Describe(TimeSpan t) => Format.RelativeTime.Span(t);
}
