// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System;
using System.IO;
using Grog.Core.Manifest;
using Grog.Core.Storage;
using Grog.Core.Tests.Framework;
using Grog.Core.Volumes;

[NewBatch]
[Trait("guards")]
public class LocationGuardTests
{
    static (VolumeService svc, string dir) New()
    {
        var dir = Directory.CreateTempSubdirectory("grog-guard-").FullName;
        Environment.SetEnvironmentVariable("GROG_CONFIG_DIR", Path.Combine(dir, "_cfg"));
        var store = new JsonManifestStore(GrogPaths.Resolve(dir));
        store.LoadAsync().GetAwaiter().GetResult();
        return (new VolumeService(store), dir);
    }

    [Test] void AddRoot_InsideExistingRoot_IsRejected()
    {
        var (svc, dir) = New();
        try
        {
            svc.AddRoot(Path.Combine(dir, "outer"));
            var ex = Assert.Throws<InvalidOperationException>(
                () => svc.AddRoot(Path.Combine(dir, "outer", "inner")), "nested root must be rejected");
            Assert.True(ex.Message.Contains("inside an existing backup folder"), "explains why");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test] void AddRoot_ContainingExistingRoot_IsRejected()
    {
        var (svc, dir) = New();
        try
        {
            svc.AddRoot(Path.Combine(dir, "outer", "inner"));
            Assert.Throws<InvalidOperationException>(
                () => svc.AddRoot(Path.Combine(dir, "outer")), "a root that swallows an existing root is rejected");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test] void AddRoot_SiblingFolders_Allowed()
    {
        var (svc, dir) = New();
        try
        {
            svc.AddRoot(Path.Combine(dir, "a"));
            var b = svc.AddRoot(Path.Combine(dir, "b"));
            Assert.True(b is not null, "independent folders are fine");
            Assert.Equal(2, svc.Roots.Count, "both roots registered");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test] void AddRoot_SameFolderTwice_IsIdempotent()
    {
        var (svc, dir) = New();
        try
        {
            var a = svc.AddRoot(Path.Combine(dir, "a"));
            var again = svc.AddRoot(Path.Combine(dir, "a"));
            Assert.Equal(a.Id, again.Id, "same folder returns the same root, not a duplicate");
            Assert.Equal(1, svc.Roots.Count, "no duplicate added");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test] void FindSameVolume_DetectsSharedDrive()
    {
        var (svc, dir) = New();
        try
        {
            svc.AddRoot(Path.Combine(dir, "a"));
            var hit = svc.FindSameVolume(Path.Combine(dir, "b"));
            Assert.True(hit is not null, "sibling folder on the same volume is detected (warn, not block)");
        }
        finally { Directory.Delete(dir, true); }
    }
}
