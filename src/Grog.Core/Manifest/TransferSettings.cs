// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Manifest;

/// <summary>
/// How this library's transfers run: worker count, bandwidth cap, cloud-save retention. LIBRARY facts
/// (S2.3, 09-08): they lived in the App's settings file, so a scheduled <c>grogcli backup</c> ran with
/// different concurrency, no cap and no retention than the App the user had configured. The App migrates
/// its old values in once, one-way; both hosts read here.
/// </summary>
public sealed class TransferSettings
{
    /// <summary>Max concurrent downloads when <see cref="AutoConcurrency"/> is off. Kept modest: Grog is a
    /// guest on GOG's servers. Default 2 (owner call 09-01).</summary>
    public int MaxConcurrentDownloads { get; set; } = 2;

    /// <summary>The worker count follows the ConcurrencyAdvisor's read of the queue head (default ON).</summary>
    public bool AutoConcurrency { get; set; } = true;

    /// <summary>Bandwidth cap toggle + rate. The cap applies ONLY when enabled AND the rate is positive;
    /// disabled always means uncapped.</summary>
    public bool DownloadLimitEnabled { get; set; }
    public int DownloadLimitKBps { get; set; } = 1024;

    /// <summary>Cloud-save snapshots retained per game: 0 = keep all (default), N = keep the newest N.
    /// Governs FUTURE backups only; lowering it never deletes existing history.</summary>
    public int KeepCloudSaves { get; set; }

    /// <summary>The engine's limit: bytes per second, 0 = uncapped.</summary>
    public long BytesPerSecondLimit => DownloadLimitEnabled && DownloadLimitKBps > 0 ? DownloadLimitKBps * 1024L : 0;

    /// <summary>The fixed worker count for a run, or null when Auto (the advisor decides).</summary>
    public int? FixedConcurrency => AutoConcurrency ? null : System.Math.Clamp(MaxConcurrentDownloads, 1, 8);
}
