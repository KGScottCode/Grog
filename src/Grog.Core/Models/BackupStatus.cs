// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Threading;
using System.Text.Json.Serialization;

namespace Grog.Core.Models;

/// <summary>Per-game rollup -- the traffic light.</summary>
public enum BackupStatus
{
    Unknown = 0,
    [JsonStringEnumMemberName("NotDownloaded")] NotBackedUp = 1,   // red: nothing local
    Partial = 2,           // yellow: some files present
    [JsonStringEnumMemberName("UpdateAvailable")] Outdated = 3,    // yellow: complete but outdated
    Complete = 4,          // green: everything present + verified
    [JsonStringEnumMemberName("Error")] Corrupt = 5,               // red: corrupt files detected
    Missing = 6,           // amber: was backed up, now gone from disk (re-download)
}
