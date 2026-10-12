// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Manifest;

public sealed class TransferSettings
{
    public int MaxConcurrentDownloads { get; set; } = 2;

    public bool AutoConcurrency { get; set; } = true;

    public bool DownloadLimitEnabled { get; set; }
    public int DownloadLimitKBps { get; set; } = 1024;

    public int KeepCloudSaves { get; set; }

    public long BytesPerSecondLimit => DownloadLimitEnabled && DownloadLimitKBps > 0 ? DownloadLimitKBps * 1024L : 0;

    public int? FixedConcurrency => AutoConcurrency ? null : System.Math.Clamp(MaxConcurrentDownloads, 1, 8);
}
