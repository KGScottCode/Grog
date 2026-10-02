// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System;
using System.IO;
using System.Threading.Tasks;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Storage;
using Grog.Core.Tests.Framework;
using Grog.Core.Volumes;

// Sweep 2 #7: `grogcli delete` cleared State and LocalSizeBytes by hand and nothing else. It now goes through
// LocalDeletion, so the record is back to never-touched and a queued file is left to its download.
[Trait("deletion")]
public class DeleteBackupServiceTests
{
    static (JsonManifestStore Store, string Root) NewStore()
    {
        var root = Directory.CreateTempSubdirectory("grog-delsvc-").FullName;
        Environment.SetEnvironmentVariable("GROG_CONFIG_DIR", Path.Combine(root, "_cfg"));
        var store = new JsonManifestStore(GrogPaths.Resolve(root));
        store.LoadAsync().GetAwaiter().GetResult();
        return (store, root);
    }

    static GameFile Put(string root, LibraryItem item, string key, string rel)
    {
        var abs = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllText(abs, "bytes");
        var f = new GameFile { GameGogId = item.GogId, FileKey = key, Name = Path.GetFileName(rel), Kind = FileKind.Installer,
                               State = FileState.Verified, LocalRelativePath = rel, LocalSizeBytes = 5,
                               DownloadedAt = DateTimeOffset.UtcNow, LastVerifiedAt = DateTimeOffset.UtcNow };
        item.Files.Add(f);
        return f;
    }

    [Test]
    async Task Delete_GoesThroughLocalDeletion()
    {
        var (store, root) = NewStore();
        try
        {
            var layout = new BackupLayout(store.Current, root);
            var item = new LibraryItem { GogId = 1, Title = "One", Slug = "one" };
            store.Current.Items.Add(item);
            var a = Put(root, item, "/a", "Games/one/setup.exe");
            var q = Put(root, item, "/q", "Games/one/patch.exe");
            store.Current.Downloads.Enqueue(1, "/q");

            var svc = new DeleteBackupService(store, layout);
            var plan = svc.PlanFor(_ => true);
            var res = await svc.ExecuteDetailedAsync(plan);

            Assert.Equal(1, res.Deleted, "the idle file");
            Assert.Equal(1, res.Skipped, "the queued file is left to its download");
            Assert.True(a.LocalRelativePath is null && a.RootId is null && a.LastVerifiedAt is null && a.DownloadedAt is null,
                "the record is back to never-touched, not just a state flip");
            Assert.Equal(FileState.NotBackedUp, a.State);
            Assert.True(File.Exists(Path.Combine(root, "Games", "one", "patch.exe")), "queued bytes stay");
            Assert.Equal(FileState.Verified, q.State, "and so does its record");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
