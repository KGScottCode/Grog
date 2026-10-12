// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using Grog.Core.Models;

namespace Grog.Core.Sync;

public enum RetryDecision
{
    Retry = 0,
    GiveUp = 1,
}

public static class RetryPolicy
{
    public const int MaxAttempts = 3;

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

    public static void RecordSuccess(GameFile f) => f.FailedAttempts = 0;

    public static void ManualRetry(GameFile f)
    {
        f.FailedAttempts = 0;
        if (f.State == FileState.Corrupt) f.State = FileState.NotBackedUp;
    }

    public static bool IsExhausted(GameFile f) => f.FailedAttempts >= MaxAttempts;
}
