// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using Grog.Core.Storage;
using Grog.Core.Tests.Framework;

public class StorageReportTests
{
    const long GB = 1L << 30;

    [Test]
    void Judge_ComfortableWhenPlentyOfRoom()
    {
        // need 100 GB, have 1 TB free → lots of headroom
        Assert.True(StorageReport.Judge(100 * GB, 1024 * GB) == StorageReport.Headroom.Comfortable, "plenty of room = green");
    }

    [Test]
    void Judge_TightWhenFitsButLittleHeadroom()
    {
        // need 90 GB, have 100 GB free → fits, but only 10% left after
        Assert.True(StorageReport.Judge(90 * GB, 100 * GB) == StorageReport.Headroom.Tight, "fits but tight = yellow");
    }

    [Test]
    void Judge_WontFitWhenTooBig()
    {
        Assert.True(StorageReport.Judge(200 * GB, 100 * GB) == StorageReport.Headroom.WontFit, "too big = red");
    }

    [Test]
    void Judge_NothingNeededIsComfortable()
    {
        Assert.True(StorageReport.Judge(0, 100 * GB) == StorageReport.Headroom.Comfortable, "nothing to do = green");
    }

    [Test]
    void Judge_UnknownWhenNoFreeInfo()
    {
        Assert.True(StorageReport.Judge(100 * GB, 0) == StorageReport.Headroom.Unknown, "no free info = unknown");
    }

    [Test]
    void FitColor_MapsToExpectedHexes()
    {
        Assert.Equal("#6FAE72", StorageReport.HeadroomColorHex(StorageReport.Headroom.Comfortable), "green");
        Assert.Equal("#D9A441", StorageReport.HeadroomColorHex(StorageReport.Headroom.Tight), "amber");
        Assert.Equal("#C9605C", StorageReport.HeadroomColorHex(StorageReport.Headroom.WontFit), "red");
    }

    [Test]
    void DrivesFor_CountsWhatIsActuallyInGrogTmp_NotAModelledPartial()
    {
        // Owner-caught 09-04: partial bytes were modelled from the manifest (and credited to the wrong root
        // while a file streamed to the secondary). The .part directory IS the fact: measure it per root.
        var m = new Grog.Core.Manifest.LibraryManifest();
        m.Roots.Add(new Grog.Core.Models.BackupRoot { Id = "p", Label = "Primary" });
        m.PrimaryRootId = "p";
        var f = new Grog.Core.Models.GameFile { GameGogId = 1, FileKey = "k", ExpectedSizeBytes = 1000, HasPartial = true, PartialBytes = 999_999 };
        m.Items.Add(new Grog.Core.Models.LibraryItem { GogId = 1, Title = "G", Files = { f } });

        var dir = System.IO.Directory.CreateTempSubdirectory("grog-sr-").FullName;
        try
        {
            var tmp = System.IO.Directory.CreateDirectory(System.IO.Path.Combine(dir, ".grog-tmp")).FullName;
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(tmp, "a.part"), new byte[300]);
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(tmp, "b.part"), new byte[100]);   // an orphan counts too: it is on the drive
            long got = 0;
            for (int i = 0; i < 100 && got != 400; i++)   // measured off-thread: the first call kicks it, later calls read it
            { got = StorageReport.DrivesFor(m, new[] { ("p", dir) })[0].GrogBytes; if (got != 400) System.Threading.Thread.Sleep(20); }
            Assert.Equal(400L, got, "measured .grog-tmp, not the manifest's number");
        }
        finally { System.IO.Directory.Delete(dir, true); }
    }
}
