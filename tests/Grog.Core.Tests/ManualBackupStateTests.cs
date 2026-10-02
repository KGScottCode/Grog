// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System.Linq;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Sync;
using Grog.Core.Tests.Framework;

[NewBatch]
[Trait("tracking")]
public class ManualBackupStateTests
{
    static LibraryManifest OneProduct()
    {
        var item = new LibraryItem { GogId = 1, Title = "Game", Slug = "game" };
        item.Files.Add(new GameFile { GameGogId = 1, FileKey = "game/a.exe", Kind = FileKind.Installer, RootId = "r1", LocalRelativePath = "Games/game/a.exe", State = FileState.Verified });
        item.Files.Add(new GameFile { GameGogId = 1, FileKey = "game/b.bin", Kind = FileKind.Installer, RootId = "r1", LocalRelativePath = "Games/game/b.bin", State = FileState.Present });
        item.Files.Add(new GameFile { GameGogId = 1, FileKey = "game/c.bin", Kind = FileKind.Installer, RootId = "r1", LocalRelativePath = "Games/game/c.bin", State = FileState.NotBackedUp });
        var m = new LibraryManifest();
        m.Items.Add(item);
        return m;
    }

    [Test] void MarkProduct_Missing_FlipsAllFiles_KeepsLocation()
    {
        var m = OneProduct();
        var changed = ManualBackupState.MarkProduct(m, 1, FileState.Missing);

        Assert.Equal(2, changed, "the two files that HELD bytes flip to missing");
        var files = m.Items[0].Files;
        Assert.Equal(FileState.Missing, files[0].State, "verified -> missing");
        Assert.Equal(FileState.Missing, files[1].State, "downloaded -> missing");
        Assert.Equal(FileState.NotBackedUp, files[2].State, "never downloaded: nothing of ours can be missing (sweep 2 #26)");
        Assert.Equal("r1", files[0].RootId, "location kept");
        Assert.Equal("Games/game/a.exe", files[0].LocalRelativePath, "path kept");
    }

    [Test] void MarkProduct_NotBackedUp_SetsNotDownloaded()
    {
        var m = OneProduct();
        ManualBackupState.MarkProduct(m, 1, FileState.NotBackedUp);
        Assert.True(m.Items[0].Files.All(f => f.State == FileState.NotBackedUp), "all -> not downloaded");
    }

    [Test] void Unavailable_IsNeverOverwritten_AndANeverDownloadedFileIsNeverMissing()
    {
        var m = OneProduct();
        m.Items[0].Files[1].State = FileState.Unavailable;
        ManualBackupState.MarkProduct(m, 1, FileState.NotBackedUp);
        Assert.Equal(FileState.Unavailable, m.Items[0].Files[1].State, "GOG's fact stays");
        Assert.Equal(0, ManualBackupState.MarkFile(m, 1, "game/c.bin", FileState.Missing), "never downloaded");
    }

    [Test] void MarkFile_FlipsOnlyThatFile()
    {
        var m = OneProduct();
        var changed = ManualBackupState.MarkFile(m, 1, "game/a.exe", FileState.Missing);

        Assert.Equal(1, changed, "one file changed");
        Assert.Equal(FileState.Missing, m.Items[0].Files[0].State, "target flipped");
        Assert.Equal(FileState.Present, m.Items[0].Files[1].State, "sibling untouched");
    }

    [Test] void MarkFile_AlreadyInState_NoChange()
    {
        var m = OneProduct();
        Assert.Equal(0, ManualBackupState.MarkFile(m, 1, "game/c.bin", FileState.NotBackedUp), "already notdownloaded: no change");
    }
}
