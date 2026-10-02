// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using Grog.App.ViewModels;
using Grog.Core.Tests.Framework;

/// <summary>(Storage math 09-09) The Storage Status bar's split of a drive into Grog / other / free.
/// The owner spotted the bug behind these: with TWO backup folders on ONE physical drive, every row was
/// reporting the SIBLING folder's backups as "other data (not Grog's)", because "used" is a whole-volume
/// figure and only the row's own Grog bytes were being taken out of it. Invisible at a gigabyte against a
/// terabyte; hundreds of gigabytes of phantom "other data" after a real backup.</summary>
[NewBatch]
[Trait("storage")]
public class StorageBarMathTests
{
    private const long GB = 1024L * 1024 * 1024;

    private static BackupLocationRow Row(long grog, long sibling, long free, long total)
        => new("r1", "Primary", @"C:\Backup", true, true, grog, free, total, 0, "#000000")
        { SiblingGrogBytes = sibling };

    /// <summary>One folder on a drive: everything used that is not Grog's is other data. Unchanged
    /// behaviour, pinned so the sibling fix cannot quietly alter the single-folder case.</summary>
    [Test]
    void OtherBytes_OneFolderOnTheDrive()
    {
        var r = Row(grog: 100 * GB, sibling: 0, free: 700 * GB, total: 1000 * GB);
        Assert.Equal(200 * GB, r.OtherBytes, "used 300, of which 100 is Grog");
    }

    /// <summary>THE BUG: two folders on one drive share Free and Total, so "used" holds both folders' data.
    /// The sibling's backups are Grog's, not "other data".</summary>
    [Test]
    void OtherBytes_ExcludesASiblingFolderOnTheSameVolume()
    {
        // 1 TB drive, 700 GB free. Used = 300: 100 in this folder, 150 in the sibling, 50 genuinely other.
        var r = Row(grog: 100 * GB, sibling: 150 * GB, free: 700 * GB, total: 1000 * GB);
        Assert.Equal(50 * GB, r.OtherBytes, "only the genuinely foreign 50 GB counts as other");
    }

    /// <summary>The whole volume is Grog's across two folders: nothing is other data. Before the fix this
    /// reported the sibling's entire holding as foreign.</summary>
    [Test]
    void OtherBytes_IsZeroWhenGrogHoldsEverythingUsedAcrossBothFolders()
    {
        var r = Row(grog: 120 * GB, sibling: 180 * GB, free: 700 * GB, total: 1000 * GB);
        Assert.Equal(0, r.OtherBytes, "300 used, 300 of it Grog's");
    }

    /// <summary>Never negative: the byte figures come from two sources (a folder walk and the OS), so they
    /// can disagree slightly, and a negative segment would invert the bar.</summary>
    [Test]
    void OtherBytes_NeverGoesNegativeWhenTheFiguresDisagree()
    {
        var r = Row(grog: 100 * GB, sibling: 250 * GB, free: 700 * GB, total: 1000 * GB);
        Assert.Equal(0, r.OtherBytes, "clamped at zero");
    }

    /// <summary>The "drive overhead" wording is chosen from OtherBytes, so it has to follow the corrected
    /// figure: with the sibling excluded, a sliver of foreign data reads as filesystem bookkeeping rather
    /// than sending the user hunting for files that are their own backups.</summary>
    [Test]
    void OtherIsOverhead_FollowsTheCorrectedFigure()
    {
        var r = Row(grog: 100 * GB, sibling: 199 * GB, free: 700 * GB, total: 1000 * GB);
        Assert.True(r.OtherIsOverhead, "1 GB of 1000 is under 1%, so it is overhead");
        var wrong = Row(grog: 100 * GB, sibling: 0, free: 700 * GB, total: 1000 * GB);
        Assert.False(wrong.OtherIsOverhead, "200 GB of foreign data is not overhead");
    }

    /// <summary>The tooltip has to add up: it states used, then the parts. With a sibling present the parts
    /// must name it, or the arithmetic visibly fails in front of the user.</summary>
    [Test]
    void BarToolTip_NamesTheSiblingFolder()
    {
        var r = Row(grog: 100 * GB, sibling: 150 * GB, free: 700 * GB, total: 1000 * GB);
        Assert.Contains("in another folder here", r.BarToolTip, "sibling named");
        var alone = Row(grog: 100 * GB, sibling: 0, free: 700 * GB, total: 1000 * GB);
        Assert.False(alone.BarToolTip.Contains("in another folder here"), "not mentioned when there is no sibling");
    }

    // 1282: a verify on a storage shows on its card ("Verifying 37 / 192"); idle cards carry nothing.
    [Test]
    public void A_verifying_card_names_its_progress_and_an_idle_one_does_not()
    {
        var idle = Row(0, 0, 100, 100);
        Assert.False(idle.IsVerifying); Assert.Equal("", idle.VerifyText);
        var busy = idle with { VerifyText = "Verifying 37 / 192" };
        Assert.True(busy.IsVerifying);
    }
}
