// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System.Collections.Generic;

namespace Grog.Core.Guide;

public readonly record struct GuideState(
    bool HasAccount,
    bool HasLibrary,
    bool HasBackedUpAnything,
    string CurrentView,
    bool IsScanning = false,
    bool RunStarted = false,
    bool ShowingItemCount = false,
    bool ScanRan = false,
    bool ItemCountSeen = false,
    IReadOnlySet<string>? Acked = null,
    bool FirstFileBackedUp = false,
    bool HasCloudSaves = false,
    bool HasDemoFile = false)
{
    public bool Ack(string key) => Acked is not null && Acked.Contains(key);
}
