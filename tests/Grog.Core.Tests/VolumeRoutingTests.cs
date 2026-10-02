// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System.IO;
using System.Linq;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Storage;
using Grog.Core.Tests.Framework;
using Grog.Core.Volumes;

public class VolumeRoutingTests
{
    [Test]
    void RoutingPolicy_RoleMapping_GamesVsExtras()
    {
        Assert.Equal(ContentRole.Games, RoutingPolicy.RoleOf(FileKind.Installer), "installer→games");
        Assert.Equal(ContentRole.Games, RoutingPolicy.RoleOf(FileKind.Patch), "patch→games");
        Assert.Equal(ContentRole.Games, RoutingPolicy.RoleOf(FileKind.DlcInstaller), "dlc installer→games");
        Assert.Equal(ContentRole.Extras, RoutingPolicy.RoleOf(FileKind.Extra), "extra→extras");
    }

    [Test]
    void RoutingPolicy_RoleDefault()
    {
        var p = new RoutingPolicy();
        p.SetRole(ContentRole.Games, "fast");
        p.SetRole(ContentRole.Extras, "slow");

        Assert.Equal("fast", p.ResolveRootId(100, FileKind.Installer, "primary", ExtrasPlacement.SeparateByGame), "installer→games root");
        Assert.Equal("slow", p.ResolveRootId(100, FileKind.Extra, "primary", ExtrasPlacement.SeparateByGame), "extra→extras root");
    }

    [Test]
    void RoutingPolicy_FallsBackToPrimary_WhenUnset()
    {
        var p = new RoutingPolicy();
        Assert.Equal("primary", p.ResolveRootId(1, FileKind.Installer, "primary", ExtrasPlacement.SeparateByGame), "no rule → primary");
    }

    [Test]
    void Layout_CreatesPrimaryRoot_AndResolves()
    {
        var root = Directory.CreateTempSubdirectory("grog-vol-").FullName;
        try
        {
            var m = new LibraryManifest();
            var layout = new BackupLayout(m, root);

            Assert.True(m.PrimaryRootId is not null, "primary root created");
            Assert.Equal(1, m.Roots.Count, "one root");
            Assert.True(layout.IsOnline(m.PrimaryRootId), "primary online");


            var file = new GameFile { GameGogId = 5, LocalRelativePath = Path.Combine("game", "a.bin") };
            var resolved = layout.ResolvePath(file);
            Assert.Equal(Path.Combine(Path.GetFullPath(root), "game", "a.bin"), resolved, "resolves under primary");
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    void Layout_TargetDir_RoutesExtrasToExtrasSubfolder()
    {
        var root = Directory.CreateTempSubdirectory("grog-vol2-").FullName;
        try
        {
            var m = new LibraryManifest();
            var layout = new BackupLayout(m, root);

            var ost = new GameFile { Kind = FileKind.Extra, Name = "ost.zip", ExtraType = "audio" };
            var instDir = layout.ResolveTargetDir(9, "witcher", FileKind.Installer, out _);
            var extraDir = layout.ResolveTargetDir(9, "witcher", FileKind.Extra, out _, ost);

            Assert.Equal(Path.Combine(Path.GetFullPath(root), "Games", "witcher"), instDir, "installer → Games/<slug>");
            // WITH-GAME is the v0.1.0 default (LibraryManifest.ExtrasLayout = WithGame): a FRESH library puts
            // extras flat inside the game's own folder. The separate-tree layouts still work; pre-v6
            // manifests keep their bool-derived separate layout (see JsonManifestStore.MigrateSchema v6).
            Assert.Equal(Path.Combine(Path.GetFullPath(root), "Games", "witcher", "Extras"), extraDir, "extra → Games/<slug>/Extras (with-game default)");
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    void Layout_OfflineRoot_ResolvesNull()
    {
        var root = Directory.CreateTempSubdirectory("grog-vol3-").FullName;
        try
        {
            var m = new LibraryManifest();
            // Add a second root that points nowhere real → offline.
            var ghost = new BackupRoot { Label = "Ghost", PathHint = Path.Combine(root, "nonexistent-drive") };
            m.Roots.Add(ghost);
            var layout = new BackupLayout(m, root);

            Assert.True(!layout.IsOnline(ghost.Id), "ghost root offline");
            Assert.Equal(RootState.Offline, ghost.State, "state offline");

            var file = new GameFile { RootId = ghost.Id, LocalRelativePath = "x/y.bin" };
            Assert.True(layout.ResolvePath(file) is null, "offline root resolves null");
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    async System.Threading.Tasks.Task Migration_V2ToV3_SetsSchemaAndKeepsResolving()
    {
        var root = Directory.CreateTempSubdirectory("grog-vol4-").FullName;
        Environment.SetEnvironmentVariable("GROG_CONFIG_DIR", Path.Combine(root, "_cfg"));
        try
        {
            var paths = GrogPaths.Resolve(root);
            // v2 manifest with a downloaded file, no roots -- at the config location.
            await File.WriteAllTextAsync(paths.ManifestPath,
                "{\"SchemaVersion\":2,\"Items\":[{\"GogId\":7,\"Title\":\"G\",\"Files\":[" +
                "{\"GameGogId\":7,\"LocalRelativePath\":\"g/setup.exe\",\"State\":\"Downloaded\"}]}]}");

            var store = new JsonManifestStore(paths);
            await store.LoadAsync();
            Assert.Equal(6, store.Current.SchemaVersion, "migrated to current (6)");

            // Layout binds primary; the file (RootId null) still resolves via PrimaryRootId fallback.
            var layout = new BackupLayout(store.Current, root);
            var f = store.Current.Items[0].Files[0];
            var resolved = layout.ResolvePath(f);
            Assert.Equal(Path.Combine(Path.GetFullPath(root), "g", "setup.exe"), resolved, "resolves post-migration");
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    void Binding_a_folder_creates_nothing_until_the_user_confirms_it()
    {
        // Grog binds a DEFAULT folder automatically at first launch, before the user has agreed to anything.
        // Scaffolding from the constructor therefore wrote Games/Extras/Cloud Saves into a location that was
        // still just a proposal -- and left them there if the answer turned out to be somewhere else.
        var dir = Path.Combine(Path.GetTempPath(), "grog-scaffold-" + System.Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var m = new LibraryManifest();
            var layout = new BackupLayout(m, dir);
            Assert.True(!Directory.Exists(Path.Combine(dir, "Games")), "binding alone creates no Games folder");
            Assert.True(!Directory.Exists(Path.Combine(dir, "Extras")), "binding alone creates no Extras folder");
            Assert.True(!Directory.Exists(Path.Combine(dir, "Cloud Saves")), "binding alone creates no Cloud Saves folder");

            // Confirming is the deliberate act. With the WithGame default there is no top-level Extras tree,
            // so scaffolding it would leave a permanently empty folder; the separate layouts create it below.
            layout.ScaffoldCategoryFolders();
            Assert.True(Directory.Exists(Path.Combine(dir, "Games")), "confirming creates Games");
            Assert.True(!Directory.Exists(Path.Combine(dir, "Extras")), "with-game default: no top-level Extras");
            Assert.True(Directory.Exists(Path.Combine(dir, "Cloud Saves")), "confirming creates Cloud Saves");

            m.SetExtrasLayout(ExtrasPlacement.SeparateByGame);
            layout.ScaffoldCategoryFolders();
            Assert.True(Directory.Exists(Path.Combine(dir, "Extras")), "separate-tree layout creates Extras");

            layout.ScaffoldCategoryFolders();   // idempotent: it is called again as a backstop before any run
            Assert.True(Directory.Exists(Path.Combine(dir, "Games")), "calling it twice is harmless");
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }
}
