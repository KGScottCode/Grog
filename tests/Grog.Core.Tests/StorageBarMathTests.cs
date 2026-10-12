// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using Grog.App.ViewModels;
using Grog.Core.Tests.Framework;

[NewBatch]
[Trait("storage")]
public class StorageBarMathTests
{
    private const long GB = 1024L * 1024 * 1024;

    private static BackupLocationRow Row(long grog, long sibling, long free, long total)
        => new("r1", "Primary", @"C:\Backup", true, true, grog, free, total, 0, "#000000")
        { SiblingGrogBytes = sibling };

    [Test]
    void OtherBytes_OneFolderOnTheDrive()
    {
        var r = Row(grog: 100 * GB, sibling: 0, free: 700 * GB, total: 1000 * GB);
        Assert.Equal(200 * GB, r.OtherBytes, "used 300, of which 100 is Grog");
    }

    [Test]
    void OtherBytes_ExcludesASiblingFolderOnTheSameVolume()
    {
        // 1 TB drive, 700 GB free. Used = 300: 100 in this folder, 150 in the sibling, 50 genuinely other.
        var r = Row(grog: 100 * GB, sibling: 150 * GB, free: 700 * GB, total: 1000 * GB);
        Assert.Equal(50 * GB, r.OtherBytes, "only the genuinely foreign 50 GB counts as other");
    }

    [Test]
    void OtherBytes_IsZeroWhenGrogHoldsEverythingUsedAcrossBothFolders()
    {
        var r = Row(grog: 120 * GB, sibling: 180 * GB, free: 700 * GB, total: 1000 * GB);
        Assert.Equal(0, r.OtherBytes, "300 used, 300 of it Grog's");
    }

    [Test]
    void OtherBytes_NeverGoesNegativeWhenTheFiguresDisagree()
    {
        var r = Row(grog: 100 * GB, sibling: 250 * GB, free: 700 * GB, total: 1000 * GB);
        Assert.Equal(0, r.OtherBytes, "clamped at zero");
    }

    [Test]
    void OtherIsOverhead_FollowsTheCorrectedFigure()
    {
        var r = Row(grog: 100 * GB, sibling: 199 * GB, free: 700 * GB, total: 1000 * GB);
        Assert.True(r.OtherIsOverhead, "1 GB of 1000 is under 1%, so it is overhead");
        var wrong = Row(grog: 100 * GB, sibling: 0, free: 700 * GB, total: 1000 * GB);
        Assert.False(wrong.OtherIsOverhead, "200 GB of foreign data is not overhead");
    }

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
