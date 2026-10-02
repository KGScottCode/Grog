// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System;
using System.IO;
using Grog.Core.Auth;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Storage;
using Grog.Core.Tests.Framework;

public class StoragePathsTests
{
    // Runs an action with GROG_CONFIG_DIR pointed at an isolated temp dir (so tests never touch %APPDATA%).
    private static void WithConfigDir(Action<string> body)
    {
        var cfg = Directory.CreateTempSubdirectory("grog-cfg-").FullName;
        var prev = Environment.GetEnvironmentVariable("GROG_CONFIG_DIR");
        Environment.SetEnvironmentVariable("GROG_CONFIG_DIR", cfg);
        try { body(cfg); }
        finally { Environment.SetEnvironmentVariable("GROG_CONFIG_DIR", prev); try { Directory.Delete(cfg, true); } catch { } }
    }

    [Test]
    void Resolve_ConfigDir_IsProfileFolder_NotBackupRoot()
    {
        WithConfigDir(cfg =>
        {
            var root = Directory.CreateTempSubdirectory("grog-paths-").FullName;
            try
            {
                var p = GrogPaths.Resolve(root);
                Assert.Equal(Path.GetFullPath(root), p.BackupRoot, "backup root honored");
                Assert.Equal(cfg, p.ConfigDir, "config dir is the profile folder, not the backup root");
                Assert.True(p.ManifestPath.StartsWith(cfg), "manifest lives in the config dir");
                Assert.True(p.TokensPath.StartsWith(cfg), "tokens live in the config dir");
                Assert.False(Directory.Exists(Path.Combine(root, ".grog")), "no .grog folder created on the drive");
            }
            finally { Directory.Delete(root, true); }
        });
    }

    [Test]
    void Resolve_MigratesLegacyDriveState_IntoProfile()
    {
        WithConfigDir(cfg =>
        {
            var root = Directory.CreateTempSubdirectory("grog-mig-").FullName;
            try
            {
                // Simulate an older build's drive-side state.
                var legacy = Path.Combine(root, ".grog");
                Directory.CreateDirectory(legacy);
                File.WriteAllText(Path.Combine(legacy, "grog-manifest.json"), "{\"SchemaVersion\":4,\"Items\":[]}");
                File.WriteAllText(Path.Combine(legacy, "tokens.json"), "legacy-token");

                var p = GrogPaths.Resolve(root);   // migration runs here
                Assert.True(File.Exists(Path.Combine(cfg, "grog-manifest.json")), "manifest copied into profile");
                Assert.True(File.Exists(Path.Combine(cfg, "tokens.json")), "tokens copied into profile");
            }
            finally { Directory.Delete(root, true); }
        });
    }

    [Test]
    async System.Threading.Tasks.Task Manifest_SaveThenLoad_RoundTrips()
    {
        await System.Threading.Tasks.Task.CompletedTask;
        WithConfigDir(_ =>
        {
            var root = Directory.CreateTempSubdirectory("grog-rt-").FullName;
            try
            {
                var paths = GrogPaths.Resolve(root);
                var store = new JsonManifestStore(paths);
                store.LoadAsync().GetAwaiter().GetResult();
                store.Current.Items.Add(new LibraryItem { GogId = 7, Title = "Round Trip" });
                store.SaveAsync().GetAwaiter().GetResult();
                Assert.True(File.Exists(paths.ManifestPath), "saved into the config dir");

                var store2 = new JsonManifestStore(paths);
                store2.LoadAsync().GetAwaiter().GetResult();
                Assert.Equal(1, store2.Current.Items.Count, "reloaded");
                Assert.Equal(6, store2.Current.SchemaVersion, "schema v6 (extras-layout migration bumps on load)");
            }
            finally { Directory.Delete(root, true); }
        });
    }

    [Test]
    async System.Threading.Tasks.Task Tokens_SaveThenLoad_InConfigDir()
    {
        await System.Threading.Tasks.Task.CompletedTask;
        WithConfigDir(_ =>
        {
            var root = Directory.CreateTempSubdirectory("grog-tok-").FullName;
            try
            {
                var paths = GrogPaths.Resolve(root);
                var store = new FileTokenStore(paths.TokensPath);
                Assert.True(store.LoadAsync().GetAwaiter().GetResult() is null, "no token yet");
                store.SaveAsync(new AuthSession("a", "r", DateTimeOffset.UtcNow.AddHours(1), "uid")).GetAwaiter().GetResult();
                Assert.True(File.Exists(paths.TokensPath), "token saved in config dir");
            }
            finally { Directory.Delete(root, true); }
        });
    }
}
