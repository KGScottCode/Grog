// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using Grog.Core.Models;
using Grog.Core.Sync;
using Grog.Core.Tests.Framework;

[NewBatch]
[Trait("retry")]
public class RetryPolicyTests
{
    private static GameFile F() => new() { FileKey = "k", State = FileState.NotBackedUp };

    [Test] void FirstTwoFailuresRetry()
    {
        var f = F();
        Assert.Equal(RetryDecision.Retry, RetryPolicy.RecordFailure(f), "1st failure retries");
        Assert.Equal(1, f.FailedAttempts, "count = 1");
        Assert.Equal(RetryDecision.Retry, RetryPolicy.RecordFailure(f), "2nd failure retries");
        Assert.Equal(2, f.FailedAttempts, "count = 2");
        Assert.True(!RetryPolicy.IsExhausted(f), "not exhausted yet");
    }

    [Test] void ThirdFailureGivesUpAndMarksCorrupt()
    {
        var f = F();
        RetryPolicy.RecordFailure(f);
        RetryPolicy.RecordFailure(f);
        Assert.Equal(RetryDecision.GiveUp, RetryPolicy.RecordFailure(f), "3rd failure gives up");
        Assert.Equal(FileState.Corrupt, f.State, "marked corrupt");
        Assert.True(RetryPolicy.IsExhausted(f), "exhausted");
    }

    [Test] void SuccessResetsTheCount()
    {
        var f = F();
        RetryPolicy.RecordFailure(f);
        RetryPolicy.RecordFailure(f);
        RetryPolicy.RecordSuccess(f);
        Assert.Equal(0, f.FailedAttempts, "clean success clears strikes");
        // and a subsequent failure starts fresh -> retries, doesn't immediately condemn
        Assert.Equal(RetryDecision.Retry, RetryPolicy.RecordFailure(f), "starts over");
    }

    [Test] void ManualRetryResetsAndClearsCorrupt()
    {
        var f = F();
        RetryPolicy.RecordFailure(f);
        RetryPolicy.RecordFailure(f);
        RetryPolicy.RecordFailure(f);   // now Corrupt, exhausted
        RetryPolicy.ManualRetry(f);
        Assert.Equal(0, f.FailedAttempts, "manual retry resets count");
        Assert.Equal(FileState.NotBackedUp, f.State, "corrupt cleared back to not-downloaded");
        Assert.True(!RetryPolicy.IsExhausted(f), "recoverable again");
    }

    [Test] void ManualRetryLeavesNonCorruptStateAlone()
    {
        // Retrying something that isn't condemned (e.g. the user forcing a re-check) shouldn't clobber a
        // meaningful state -- only Corrupt is rewound.
        var f = new GameFile { FileKey = "k", State = FileState.Verified, FailedAttempts = 1 };
        RetryPolicy.ManualRetry(f);
        Assert.Equal(0, f.FailedAttempts, "count reset");
        Assert.Equal(FileState.Verified, f.State, "non-corrupt state untouched");
    }
}
