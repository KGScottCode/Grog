// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System.Linq;
using Grog.Core.Storage;
using Grog.Core.Tests.Framework;

public class BackupLocationTests
{
    [Test]
    void Suggest_AlwaysOffersDocumentsAndCustom()
    {
        var opts = BackupLocationSuggester.Suggest();
        Assert.True(opts.Any(o => o.Kind == BackupLocationSuggester.Kind.Documents), "has Documents");
        Assert.True(opts.Any(o => o.Kind == BackupLocationSuggester.Kind.BesideExe), "has BesideExe");
        Assert.True(opts.Any(o => o.Kind == BackupLocationSuggester.Kind.Custom), "has Custom");
    }

    [Test]
    void Suggest_ExactlyOneDefault()
    {
        var opts = BackupLocationSuggester.Suggest();
        Assert.Equal(1, opts.Count(o => o.IsDefault), "exactly one option is the default");
    }

    [Test]
    void Suggest_CustomOptionHasNoPresetPath()
    {
        var custom = BackupLocationSuggester.Suggest().First(o => o.Kind == BackupLocationSuggester.Kind.Custom);
        Assert.Equal("", custom.Path, "custom path is filled in by the UI, not preset");
    }

    [Test]
    void Suggest_PathsEndWithGogBackupFolder()
    {
        foreach (var o in BackupLocationSuggester.Suggest())
            if (o.Kind != BackupLocationSuggester.Kind.Custom)
                Assert.True(o.Path.EndsWith(BackupLocationSuggester.FolderName),
                    $"{o.Kind} path ends with the GOG_Backup folder");
    }

    [Test]
    void Format_CompactPair_SharesOneUnit_OneDecimal_NoneWhenWhole()
    {
        // The CLI download renderer's fixed right column (was a private copy in Program.cs).
        Assert.Equal("71.5/87 MB", Grog.Core.Format.ByteFormat.CompactPair(74973184, 91226112), "MB pair, one decimal");
        Assert.Equal("0/512 KB", Grog.Core.Format.ByteFormat.CompactPair(0, 512L << 10), "whole values drop the decimal");
        Assert.Equal("0.5/2 GB", Grog.Core.Format.ByteFormat.CompactPair(1L << 29, 2L << 30), "unit follows the total");
        Assert.Equal("10/10 B", Grog.Core.Format.ByteFormat.CompactPair(10, 10), "bytes");
    }

    [Test]
    void Format_HumanReadable()
    {
        // Two decimals ALWAYS above the byte tier (2026-08-29): "1.00 GB" not "1 GB", so readouts
        // match ByteFormat.Size's texture and a creeping figure changes visibly every tick.
        Assert.Equal("1.00 GB", Grog.Core.Format.ByteFormat.Size(1L << 30), "1 GB");
        Assert.Equal("2.000 TB", Grog.Core.Format.ByteFormat.Size(2L << 40), "2 TB (three decimals: ~1 GB steps)");
        Assert.Equal("512.00 MB", Grog.Core.Format.ByteFormat.Size(512L << 20), "512 MB");
        // KB tier: without it a cloud save read as a raw byte count ("341060 B").
        Assert.Equal("4.00 KB", Grog.Core.Format.ByteFormat.Size(4L << 10), "4 KB");
        Assert.Equal("333.07 KB", Grog.Core.Format.ByteFormat.Size(341060), "341060 bytes reads as KB");
        // Below 1 KB still reads in bytes -- a 21-byte save should not become "0.0 KB".
        Assert.Equal("21 B", Grog.Core.Format.ByteFormat.Size(21), "21 B");
        Assert.Equal("1023 B", Grog.Core.Format.ByteFormat.Size(1023), "just under 1 KB");
    }
}
