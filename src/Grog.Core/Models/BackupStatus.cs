// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Threading;
using System.Text.Json.Serialization;

namespace Grog.Core.Models;

public enum BackupStatus
{
    Unknown = 0,
    [JsonStringEnumMemberName("NotDownloaded")] NotBackedUp = 1,    // red
    Partial = 2,                                                    // yellow
    [JsonStringEnumMemberName("UpdateAvailable")] Outdated = 3,     // yellow
    Complete = 4,                                                   // green
    [JsonStringEnumMemberName("Error")] Corrupt = 5,                // red
    Missing = 6,                                                    // amber
}
