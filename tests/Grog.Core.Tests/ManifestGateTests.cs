// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Storage;
using Grog.Core.Tests.Framework;

/// <summary>The manifest gate (09-08): mutators and the store's serialize share one door, so a save never
/// sees a torn graph and never has to retry or give up.</summary>
[Trait("manifest")]
public class ManifestGateTests
{
    static (JsonManifestStore store, string dir) New()
    {
        var dir = Directory.CreateTempSubdirectory("grog-gate-").FullName;
        Environment.SetEnvironmentVariable("GROG_CONFIG_DIR", Path.Combine(dir, "_cfg"));
        var store = new JsonManifestStore(GrogPaths.Resolve(dir));
        store.LoadAsync().GetAwaiter().GetResult();
        return (store, dir);
    }

    [Test] void Gate_IsReentrant_OnTheOwningThread()
    {
        var (store, dir) = New();
        try
        {
            store.Mutate(m =>
            {
                Assert.True(store.Gate.IsHeld, "held inside Mutate");
                store.Mutate(m2 => m2.Items.Add(new LibraryItem { GogId = 1, Title = "nested" }));   // must not deadlock
            });
            Assert.False(store.Gate.IsHeld, "released after");
            Assert.Equal(1, store.Current.Items.Count);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test] async Task A_manifest_from_a_newer_Grog_is_refused_not_recovered()
    {
        // Version ceiling (09-08): a higher SchemaVersion is a newer build's file, not corruption. It must not
        // be "recovered" from .bak, migrated downward, or saved over; both files stay exactly as found.
        var dir = Directory.CreateTempSubdirectory("grog-ceiling-").FullName;
        Environment.SetEnvironmentVariable("GROG_CONFIG_DIR", Path.Combine(dir, "_cfg"));
        try
        {
            var paths = GrogPaths.Resolve(dir);
            Directory.CreateDirectory(Path.GetDirectoryName(paths.ManifestPath)!);
            var newer = "{\"SchemaVersion\": " + (LibraryManifest.CurrentSchemaVersion + 5) + ", \"Items\": [] }";
            File.WriteAllText(paths.ManifestPath, newer);
            File.WriteAllText(paths.ManifestPath + ".bak", "{\"SchemaVersion\": 6, \"Items\": [] }");
            var store = new JsonManifestStore(paths);
            ManifestTooNewException? ex = null;
            try { await store.LoadAsync(); } catch (ManifestTooNewException e) { ex = e; }
            Assert.True(ex is not null, "refused with the message the host shows");
            Assert.True(ex!.Message.Contains("newer version of Grog"), ex.Message);
            Assert.Equal(newer, File.ReadAllText(paths.ManifestPath), "the newer file is untouched");
            Assert.False(File.Exists(paths.ManifestPath + ".corrupt"), "and never quarantined as corrupt");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test] async Task TransferSettings_RoundTrip_AndDeriveTheEngineValues()
    {
        // (S2.3) worker count, cap and cloud retention are library facts: saved with the manifest, read by
        // both hosts, and turned into engine inputs in ONE place.
        var (store, dir) = New();
        try
        {
            var t = store.Current.Transfer;
            Assert.True(t.AutoConcurrency && t.FixedConcurrency is null, "Auto by default: the advisor decides");
            Assert.Equal(0L, t.BytesPerSecondLimit, "no cap by default");
            store.Mutate(m => { m.Transfer.AutoConcurrency = false; m.Transfer.MaxConcurrentDownloads = 4; m.Transfer.DownloadLimitEnabled = true; m.Transfer.DownloadLimitKBps = 2048; m.Transfer.KeepCloudSaves = 3; });
            Assert.Equal(4, store.Current.Transfer.FixedConcurrency, "off: the typed count, clamped 1..8");
            Assert.Equal(2048L * 1024, store.Current.Transfer.BytesPerSecondLimit, "KB/s to bytes/s");
            await store.SaveAsync();
            var again = new JsonManifestStore(GrogPaths.Resolve(dir));
            await again.LoadAsync();
            Assert.Equal(3, again.Current.Transfer.KeepCloudSaves, "persisted with the library");
            store.Mutate(m => m.Transfer.DownloadLimitEnabled = false);
            Assert.Equal(0L, store.Current.Transfer.BytesPerSecondLimit, "disabled always means uncapped, never a leftover number");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test] async Task Saves_Under_ConcurrentMutation_NeverSkip()
    {
        // A writer thread adds and removes items as fast as it can, all under the gate, while saves run.
        // Before the gate a save serialised the live list and hit "collection was modified"; now it waits
        // for the writer's scope and always lands.
        var (store, dir) = New();
        int skipped = 0;
        store.SaveSkipped += _ => Interlocked.Increment(ref skipped);
        try
        {
            using var stop = new CancellationTokenSource();
            var writer = Task.Run(() =>
            {
                long id = 1;
                while (!stop.IsCancellationRequested)
                    store.Mutate(m =>
                    {
                        m.Items.Add(new LibraryItem { GogId = id++, Title = "x" });
                        if (m.Items.Count > 200) m.Items.RemoveAt(0);
                    });
            });
            for (int i = 0; i < 30; i++) await store.SaveAsync();
            stop.Cancel();
            await writer;
            Assert.Equal(0, skipped, "no save gave up");
            Assert.True(File.Exists(GrogPaths.Resolve(dir).ManifestPath), "the manifest is on disk");
        }
        finally { Directory.Delete(dir, true); }
    }
}
