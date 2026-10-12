// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Threading;
using System.Text.Json.Serialization;

namespace Grog.Core.Models;

public enum FileState
{
    [JsonStringEnumMemberName("NotDownloaded")] NotBackedUp = 0,
    [JsonStringEnumMemberName("Downloaded")] Present = 3,           // bytes on disk, not yet hash-verified
    Verified = 4,
    [JsonStringEnumMemberName("UpdateAvailable")] Outdated = 5,     // local copy exists but API shows newer version
    Corrupt = 6,                                                    // failed verification
    [JsonStringEnumMemberName("MissingLocally")] Missing = 7,       // manifest says present, file not on disk
    Unavailable = 8,
}
