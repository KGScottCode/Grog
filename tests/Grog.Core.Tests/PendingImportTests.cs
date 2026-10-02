// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System;
using System.IO;
using System.Linq;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Storage;
using Grog.Core.Tests.Framework;
using Grog.Core.Verify;
using Grog.Core.Volumes;

// Storage added before the first library scan: registered now, import deferred until items exist.
[NewBatch]
[Trait("import")]
public class PendingImportTests
{
    static (JsonManifestStore store, string dir) New()
    {
        var dir = Directory.CreateTempSubdirectory("grog-pending-").FullName;
        Environment.SetEnvironmentVariable("GROG_CONFIG_DIR", Path.Combine(dir, "_cfg"));
        var store = new JsonManifestStore(GrogPaths.Resolve(dir));
        store.LoadAsync().GetAwaiter().GetResult();
        return (store, dir);
    }

    [Test] void EmptyLibrary_RegistersRoot_AndDefersImport()
    {
        var (store, dir) = New();
        try
        {
            Assert.True(PendingImports.MustDefer(store.Current), "no items: the import must wait");
            var root = new VolumeService(store).AddRoot(Path.Combine(dir, "backup"));
            PendingImports.Mark(root);
            Assert.Equal(1, store.Current.Roots.Count, "the root is registered at once");
            Assert.Equal(1, PendingImports.Waiting(store.Current).Count, "and remembered as owing an import");
            Assert.Equal(0, PendingImports.Runnable(store.Current).Count, "but nothing can run without items");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test] void PendingFlag_SurvivesSaveAndReload()
    {
        var (store, dir) = New();
        try
        {
            var root = new VolumeService(store).AddRoot(Path.Combine(dir, "backup"));
            PendingImports.Mark(root);
            store.SaveAsync().GetAwaiter().GetResult();

            var again = new JsonManifestStore(GrogPaths.Resolve(dir));
            again.LoadAsync().GetAwaiter().GetResult();
            Assert.True(again.Current.Roots.Single().PendingImport, "a restart before the scan keeps the promise");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test] void OnceItemsExist_OnlineRootBecomesRunnable_AndClearStopsIt()
    {
        var (store, dir) = New();
        try
        {
            var root = new VolumeService(store).AddRoot(Path.Combine(dir, "backup"));
            PendingImports.Mark(root);
            store.Current.Items.Add(new LibraryItem { GogId = 1, Title = "Game" });
            Assert.Equal(1, PendingImports.Runnable(store.Current).Count, "items stored: the import is due");

            PendingImports.Clear(root);
            Assert.Equal(0, PendingImports.Runnable(store.Current).Count, "cleared roots owe nothing");
            Assert.Equal(0, PendingImports.Waiting(store.Current).Count);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test] void OfflineRoot_WaitsButIsNotRunnable()
    {
        var (store, dir) = New();
        try
        {
            var root = new VolumeService(store).AddRoot(Path.Combine(dir, "backup"));
            PendingImports.Mark(root);
            store.Current.Items.Add(new LibraryItem { GogId = 1, Title = "Game" });
            root.State = RootState.Offline;
            Assert.Equal(0, PendingImports.Runnable(store.Current).Count, "an unplugged drive is not scanned");
            Assert.Equal(1, PendingImports.Waiting(store.Current).Count, "but it still owes the import");

            root.State = RootState.Online;
            Assert.Equal(1, PendingImports.Runnable(store.Current).Count, "online again: due (the runner probes the folder itself, off-thread)");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test] void SkipScanRoots_NeverOweAnImport()
    {
        var (store, dir) = New();
        try
        {
            new VolumeService(store).AddRoot(Path.Combine(dir, "backup"));   // "Skip Scan & Add": no Mark
            store.Current.Items.Add(new LibraryItem { GogId = 1, Title = "Game" });
            Assert.Equal(0, PendingImports.Runnable(store.Current).Count);
        }
        finally { Directory.Delete(dir, true); }
    }
}
