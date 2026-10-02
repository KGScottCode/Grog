// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using Grog.Core.Models;

namespace Grog.Core.Sync;

/// <summary>What to do after a file's download or verify fails.</summary>
public enum RetryDecision
{
    /// <summary>Under the strike limit -- re-queue the file for another automatic attempt.</summary>
    Retry = 0,
    /// <summary>Strike limit reached -- stop auto-retrying, mark the file Corrupt, surface it in
    /// Log/Errors. Only a manual retry (which resets the count) will move it again.</summary>
    GiveUp = 1,
}

/// <summary>
/// Per-file retry policy, in one pure place. The attempt count is persisted on the file, so failures do
/// not get a fresh three chances every launch; after <see cref="MaxAttempts"/> failures the file goes
/// Corrupt and only a manual retry (which resets the count) or a clean success clears it. This module
/// only decides and mutates counter/state; callers queue and download.
/// </summary>
public static class RetryPolicy
{
    /// <summary>Failures tolerated before giving up and marking the file Corrupt.</summary>
    public const int MaxAttempts = 3;

    /// <summary>Record a failed attempt (download or verify). Increments the persisted counter and
    /// returns whether to auto-retry or give up. On GiveUp the file is set to Corrupt here so the
    /// caller doesn't have to remember to; on Retry the state is left for the caller to re-queue.</summary>
    public static RetryDecision RecordFailure(GameFile f)
    {
        f.FailedAttempts++;
        if (f.FailedAttempts >= MaxAttempts)
        {
            f.State = FileState.Corrupt;
            return RetryDecision.GiveUp;
        }
        return RetryDecision.Retry;
    }

    /// <summary>Record a clean success (downloaded + verified). Clears the strike count so a file that
    /// once flaked isn't one failure away from being condemned later.</summary>
    public static void RecordSuccess(GameFile f) => f.FailedAttempts = 0;

    /// <summary>The user asked to retry a condemned file. Resets the strike count and clears the Corrupt
    /// state back to NotDownloaded so the normal download path will pick it up again. This is the ONLY
    /// escape from the stuck Corrupt state -- exactly the "my internet came back" recovery.</summary>
    public static void ManualRetry(GameFile f)
    {
        f.FailedAttempts = 0;
        if (f.State == FileState.Corrupt) f.State = FileState.NotBackedUp;
    }

    /// <summary>True once the file has burned through its automatic attempts and is awaiting a manual
    /// retry. Drives the Log/Errors "Fix/Retry" affordance.</summary>
    public static bool IsExhausted(GameFile f) => f.FailedAttempts >= MaxAttempts;
}
