// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System;
using System.Linq;
using Grog.Core.Models;
using Grog.Core.Sync;
using Grog.Core.Tests.Framework;

[NewBatch]
[Trait("statusrules")]
public class BackupStatusRulesTests
{
    [Test] void NeedsWork_CoversEveryUnfinishedState()
    {
        Assert.True(BackupStatus.NotBackedUp.NeedsWork(), "never fetched");
        Assert.True(BackupStatus.Partial.NeedsWork(), "some files present");
        Assert.True(BackupStatus.Corrupt.NeedsWork(), "corrupt");
        Assert.True(BackupStatus.Outdated.NeedsWork(), "stale");
        // The regression that started this: a game whose files were deleted outside Grog rolls up to
        // Missing, and the Back-up button silently stopped counting it.
        Assert.True(BackupStatus.Missing.NeedsWork(), "was backed up, now gone -- still work to do");
    }

    [Test] void NeedsWork_ExcludesSettledAndInFlight()
    {
        Assert.True(!BackupStatus.Complete.NeedsWork(), "done");
        Assert.True(!BackupStatus.Unknown.NeedsWork(), "nothing downloadable");
    }

    [Test] void NeedsFirstBackup_SeparatesUpdatesOut()
    {
        Assert.True(BackupStatus.Missing.NeedsFirstBackup(), "missing counts as not-backed-up");
        Assert.True(BackupStatus.NotBackedUp.NeedsFirstBackup(), "not downloaded");
        Assert.True(!BackupStatus.Outdated.NeedsFirstBackup(), "an update is its own bucket");
        Assert.True(!BackupStatus.Complete.NeedsFirstBackup(), "done");
    }

    [Test] void EveryStatusIsDecided()
    {
        // Adding a BackupStatus must force a decision in BackupStatusRules rather than defaulting to
        // "no work" and quietly dropping products out of the counts.
        foreach (var s in Enum.GetValues<BackupStatus>())
        {
            bool work = s.NeedsWork();
            bool settled = s.IsSettled();
            Assert.True(!(work && settled), $"{s} cannot be both outstanding and settled");
        }
        var known = new[]
        {
            BackupStatus.Unknown, BackupStatus.NotBackedUp, BackupStatus.Partial, BackupStatus.Outdated,
            BackupStatus.Complete, BackupStatus.Corrupt, BackupStatus.Missing,
        };
        Assert.Equal(known.Length, Enum.GetValues<BackupStatus>().Length,
            "a status was added without deciding what it means for action -- update BackupStatusRules and this list");
    }
}
