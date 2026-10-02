// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using Grog.Core.Download;
using Grog.Core.Models;
using Grog.Core.Tests.Framework;

/// <summary>PercentComplete is CAPPED at 100: GOG under-reports some file lengths, and a file
/// receiving past its advertised total must not render 104% while the tail arrives (owner 08-31).</summary>
[NewBatch]
[Trait("downloadpct")]
public class DownloadTaskPercentTests
{
    private static DownloadTask Task(long? total, long received)
        => new() { File = new GameFile { FileKey = "f" }, GameTitle = "T", BytesTotal = total, BytesReceived = received };

    [Test] void MidwayReadsTrue() => Assert.Equal(50.0, Task(200, 100).PercentComplete, "half done is 50");
    [Test] void OvershootCapsAt100() => Assert.Equal(100.0, Task(200, 260).PercentComplete, "past the advertised length still reads 100, never more");
    [Test] void UnknownTotalReadsZero() => Assert.Equal(0.0, Task(null, 100).PercentComplete, "no denominator, no claim");
}
