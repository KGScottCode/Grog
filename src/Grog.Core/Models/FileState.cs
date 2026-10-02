// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Threading;
using System.Text.Json.Serialization;

namespace Grog.Core.Models;

/// <summary>What Grog DURABLY knows about a file; in-flight activity belongs to <see cref="Sync.FileActivity"/>,
/// never here (a persisted "Downloading" would outlive a crash). JSON names are pinned to the original
/// spellings so existing manifests keep parsing.</summary>
public enum FileState
{
    [JsonStringEnumMemberName("NotDownloaded")] NotBackedUp = 0,
    [JsonStringEnumMemberName("Downloaded")] Present = 3,        // bytes on disk, not yet hash-verified
    Verified = 4,
    [JsonStringEnumMemberName("UpdateAvailable")] Outdated = 5,   // local copy exists but API shows newer version
    Corrupt = 6,           // failed verification
    [JsonStringEnumMemberName("MissingLocally")] Missing = 7,     // manifest says present, file not on disk
    /// <summary>GOG lists the file but refuses to serve it to this account (entitlement). Not corruption, not a
    /// retryable gap, and excluded from every completeness number -- you cannot be incomplete against something
    /// you were never offered. Cleared back to NotBackedUp by SYNC so a changed entitlement self-heals.</summary>
    Unavailable = 8,
}
