// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Threading;
using System.Text.Json.Serialization;

namespace Grog.Core.Models;

public sealed class GameFile
{
    public long GameGogId { get; set; }
    public string FileKey { get; set; } = "";       // stable identity: manualUrl path from the API
    public FileKind Kind { get; set; }
    public string Name { get; set; } = "";          // display name from API ("Setup (Part 1 of 3)", "manual (33 pages)")
    public string? ExtraType { get; set; }          // "manuals", "wallpapers", "audio", ... (extras only)
    public string Os { get; set; } = "";            // windows | mac | linux | "" for extras
    public string Language { get; set; } = "";      // "English", ... ("" for extras)
    public string? Version { get; set; }            // version string when GOG supplies one
    public long? ExpectedSizeBytes { get; set; }    // exact when known, else parsed estimate from "1.5 GB"
    public string? ExpectedMd5 { get; set; }        // from GOG's checksum XML when available

    // Local state
    public string? RootId { get; set; }             // which backup root this file lives on (multi-volume)
    public string? LocalRelativePath { get; set; }  // path relative to that root
    public string? ResolvedFileName { get; set; }    // real CDN filename (cached from resolve); used for import name-matching + faster downloads
    public long? LocalSizeBytes { get; set; }
    public DateTimeOffset? DownloadedAt { get; set; }
    public DateTimeOffset? LastVerifiedAt { get; set; }
    public FileState State { get; set; } = FileState.NotBackedUp;

    public bool IsOldVersion { get; set; }

    public bool WithdrawnByGog { get; set; }

    public bool HasPartial { get; set; }

    public long? PartialBytes { get; set; }

    public long? WireSizeBytes { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public long? PlanSizeBytes => WireSizeBytes ?? ExpectedSizeBytes;

    public string? PartialRootId { get; set; }

    public int FailedAttempts { get; set; }

    public bool RefetchRequested { get; set; }

    public void ResetLocal()
    {
        State = FileState.NotBackedUp;
        RootId = null; LocalRelativePath = null; LocalSizeBytes = null;
        HasPartial = false; PartialBytes = null; PartialRootId = null;
        DownloadedAt = null; LastVerifiedAt = null; FailedAttempts = 0;
        RefetchRequested = false;
    }

    public List<string> OwnerIds { get; set; } = new();

    public string? UnavailableReason { get; set; }

    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }
}
