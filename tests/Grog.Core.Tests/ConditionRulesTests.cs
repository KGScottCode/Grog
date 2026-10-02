// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Sync;
using Grog.Core.Tests.Framework;
using Grog.Core.Volumes;

[NewBatch]
[Trait("condition")]
public class ConditionViewTests
{
    private static GameFile F(FileState state, string? rootId = null, bool partial = false)
        => new() { State = state, RootId = rootId, HasPartial = partial, FileKey = "k" };

    // Every device is online+fixed unless a test overrides.
    private static ConditionRules.DeviceLookup Online => _ => DeviceAvailability.OnlineFixed;

    private static ConditionRules.DeviceLookup Detachable(string id)
        => rootId => rootId == id ? new DeviceAvailability(RootState.Offline, Removable: true)
                                  : DeviceAvailability.OnlineFixed;

    [Test] void PresentAndVerifiedResolveDirectly()
    {
        Assert.Equal(FileCondition.Verified, ConditionRules.Of(F(FileState.Verified), Online), "verified");
        Assert.Equal(FileCondition.Present, ConditionRules.Of(F(FileState.Present), Online), "downloaded = present");
        Assert.Equal(FileCondition.Outdated, ConditionRules.Of(F(FileState.Outdated), Online), "update avail = outdated");
        Assert.Equal(FileCondition.Corrupt, ConditionRules.Of(F(FileState.Corrupt), Online), "corrupt");
    }

    [Test] void PartialVsNotPresentByTmpFlag()
    {
        Assert.Equal(FileCondition.Partial, ConditionRules.Of(F(FileState.NotBackedUp, partial: true), Online), "has .grog-tmp");
        Assert.Equal(FileCondition.NotBackedUp, ConditionRules.Of(F(FileState.NotBackedUp), Online), "nothing on disk");
        Assert.Equal(FileCondition.Partial, ConditionRules.Of(F(FileState.NotBackedUp, partial: true), Online), "mid-download partial");
    }

    [Test] void GoneFromOnlineDriveIsMissing()
    {
        Assert.Equal(FileCondition.Missing, ConditionRules.Of(F(FileState.Missing, "P"), Online), "online drive, file gone");
    }

    [Test] void GoneFromRemovableOfflineDriveIsDetached()
    {
        // Same underlying state, but the drive it lives on is an unplugged removable -> safe, not missing.
        Assert.Equal(FileCondition.Detached,
            ConditionRules.Of(F(FileState.Missing, "USB"), Detachable("USB")), "unplugged removable");
    }

    [Test] void PresentOnDetachedRemovableReadsDetached()
    {
        Assert.Equal(FileCondition.Detached,
            ConditionRules.Of(F(FileState.Verified, "USB"), Detachable("USB")), "verified but drive unplugged");
    }

    [Test] void HasBytesCountsDetachedAsHaveIt()
    {
        Assert.True(ConditionRules.HasBytes(FileCondition.Detached), "detached bytes still count");
        Assert.True(ConditionRules.HasBytes(FileCondition.Verified), "verified counts");
        Assert.True(!ConditionRules.HasBytes(FileCondition.Missing), "missing does not count");
        Assert.True(!ConditionRules.HasBytes(FileCondition.Partial), "partial isn't a full copy");
    }

    [Test] void LookupFromManifestReadsRootFlags()
    {
        var m = new LibraryManifest();
        m.Roots.Add(new BackupRoot { Id = "USB", State = RootState.Offline, Removable = true });
        m.Roots.Add(new BackupRoot { Id = "P", State = RootState.Online, Removable = false });
        var lookup = ConditionRules.LookupFrom(m);
        Assert.True(lookup("USB").DetachedDrive, "offline removable -> detached drive");
        Assert.True(!lookup("P").DetachedDrive, "online fixed -> not detached");
        Assert.True(!lookup("unknown").DetachedDrive, "unknown root -> online-fixed default");
    }
}
