// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using Grog.App.ViewModels;
using Grog.Core.Runs;
using Grog.Core.Tests.Framework;

// Sweep 2 #25: every failed per-row cloud Download said "GOG refused the cloud request", whatever happened.
public class CloudDownloadStatusTextTests
{
    static CloudSaveEntryResult Entry(int written, int expected, string? error = null)
        => new(1, "Orc Quest", "a", written, expected, null, 0, false, error);

    [Test] void TheRecordedErrorIsTheReason()
    {
        var t = CloudSavesViewModel.DownloadStatusText("Orc Quest", Entry(0, 0, "No such host is known."));
        Assert.Contains("No such host is known. Not marked", t, "the run's own reason, one full stop");
        Assert.False(t.Contains("refused"), "GOG never saw this request");
    }

    [Test] void ARefusalStillReadsAsARefusal()
        => Assert.Contains("GOG refused", CloudSavesViewModel.DownloadStatusText("Orc Quest", Entry(0, 2)));

    [Test] void APartialSaysSo()
        => Assert.Contains("1 of 2", CloudSavesViewModel.DownloadStatusText("Orc Quest", Entry(1, 2)));

    [Test] void ACompleteCaptureIsPlain()
        => Assert.Equal("Saved 2 file(s) for Orc Quest", CloudSavesViewModel.DownloadStatusText("Orc Quest", Entry(2, 2)));
}
