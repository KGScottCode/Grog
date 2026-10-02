// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System.Linq;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Tests.Framework;
using Grog.Core.Verify;

[NewBatch]
[Trait("confirm")]
public class ConfirmDownloadedTests
{
    static (JsonManifestStore store, string root) NewManifest()
    {
        var root = Directory.CreateTempSubdirectory("grog-confirm-").FullName;
        return (new JsonManifestStore(root), root);
    }

    static async Task<GameFile> AddPresentFile(string root, LibraryItem game, string name, string text)
    {
        var path = Path.Combine(root, game.Title, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, text);
        var f = new GameFile
        {
            GameGogId = game.GogId, FileKey = "/dl/" + name, Name = name,
            LocalSizeBytes = new FileInfo(path).Length,
            LocalRelativePath = Path.GetRelativePath(root, path),
            State = FileState.Present,
        };
        game.Files.Add(f);
        return f;
    }

    [Test] async Task Confirm_ScopedToSelection_FlipsDeletedToMissing_LeavesOthers()
    {
        var (store, root) = NewManifest();
        try
        {
            await store.LoadAsync();
            var a = new LibraryItem { GogId = 1, Title = "GameA" };
            var b = new LibraryItem { GogId = 2, Title = "GameB" };
            var fa = await AddPresentFile(root, a, "a.exe", "aaaa");
            var fb = await AddPresentFile(root, b, "b.exe", "bbbb");
            store.Current.Items.Add(a);
            store.Current.Items.Add(b);
            await store.SaveAsync();

            // Delete GameA's file on disk (simulating the user deleting it in Explorer).
            File.Delete(Path.Combine(root, "GameA", "a.exe"));

            // Confirm ONLY GameA.
            var r = await new VerifyService(store, root).ConfirmAsync(new[] { a });

            Assert.Equal(1, r.Checked, "only GameA's file checked");
            Assert.Equal(1, r.Missing, "GameA file flagged missing");
            Assert.Equal(FileState.Missing, fa.State, "GameA now missing");
            Assert.Equal(FileState.Present, fb.State, "GameB untouched (not in scope)");
        }
        finally { Directory.Delete(root, true); }
    }

    [Test] async Task Confirm_PresentFile_StaysDownloaded()
    {
        var (store, root) = NewManifest();
        try
        {
            await store.LoadAsync();
            var a = new LibraryItem { GogId = 1, Title = "GameA" };
            var fa = await AddPresentFile(root, a, "a.exe", "aaaa");
            store.Current.Items.Add(a);
            await store.SaveAsync();

            var r = await new VerifyService(store, root).ConfirmAsync(new[] { a });
            Assert.Equal(1, r.SizeOnlyOk, "present + right size");
            Assert.Equal(FileState.Present, fa.State, "stays downloaded");
        }
        finally { Directory.Delete(root, true); }
    }

    [Test] async Task ConfirmFiles_SingleFile_ScopedToThatFile()
    {
        var (store, root) = NewManifest();
        try
        {
            await store.LoadAsync();
            var a = new LibraryItem { GogId = 1, Title = "GameA" };
            var f1 = await AddPresentFile(root, a, "one.exe", "one");
            var f2 = await AddPresentFile(root, a, "two.bin", "two");
            store.Current.Items.Add(a);
            await store.SaveAsync();

            File.Delete(Path.Combine(root, "GameA", "one.exe"));

            // Confirm only the single file f1 (detail-pane per-file case).
            var r = await new VerifyService(store, root).ConfirmFilesAsync(new[] { f1 }, new[] { a });
            Assert.Equal(1, r.Checked, "only the one file checked");
            Assert.Equal(FileState.Missing, f1.State, "f1 missing");
            Assert.Equal(FileState.Present, f2.State, "f2 untouched");
        }
        finally { Directory.Delete(root, true); }
    }
}
