// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Threading;
using System.Text.Json.Serialization;

namespace Grog.Core.Models;

/// <summary>One downloadable file: an installer part, patch, language pack, or extra.</summary>
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

    /// <summary>This entry is a retained PREVIOUS build (lives in <see cref="LibraryItem.OldVersionFiles"/>,
    /// not <see cref="LibraryItem.Files"/>). Real disk, but excluded from completeness/scope/queue.</summary>
    public bool IsOldVersion { get; set; }

    /// <summary>GOG stopped listing this slot while we held its bytes. The record moved to
    /// <see cref="LibraryItem.OldVersionFiles"/> so the copy stays visible to Storage and reachable by Delete;
    /// the bytes stay where they are until the user says otherwise.</summary>
    public bool WithdrawnByGog { get; set; }

    /// <summary>A resumable partial (.grog-tmp) was seen at the last scan; lets Condition render "Partial"
    /// without re-statting the disk. Cleared when the file lands or is reset.</summary>
    public bool HasPartial { get; set; }

    /// <summary>On-disk bytes while the file is incomplete (resumable partial); captured on pause/pre-empt
    /// and at scan so progress bars are instant. Null when nothing partial exists or after completion.</summary>
    public long? PartialBytes { get; set; }

    /// <summary>The file's REAL length, learned from the CDN the first time a transfer reaches it. ExpectedSizeBytes
    /// is parsed from GOG's rounded label ("1.5 GB"); every room decision prefers this once known. Carried across a
    /// sync while the version and the label stand.</summary>
    public long? WireSizeBytes { get; set; }

    /// <summary>The best size known for room arithmetic: the real length, else GOG's label.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public long? PlanSizeBytes => WireSizeBytes ?? ExpectedSizeBytes;

    /// <summary>The root whose .grog-tmp holds the partial (null on records older than 09-19: assume the routed
    /// root). The bytes already received are room spent on THAT drive only: planned onto another, the file needs
    /// its whole size there.</summary>
    public string? PartialRootId { get; set; }

    /// <summary>Failed download/verify attempts since the last clean success (B4 retry policy); persisted so a
    /// flaky file never gets infinite fresh chances. Reset on clean success or manual retry.</summary>
    public int FailedAttempts { get; set; }

    /// <summary>The user asked for FRESH bytes (Fix It, Re-download): the engine must not adopt the copy
    /// already on disk by its size -- a corrupt file has the right size and the wrong bytes. Set by
    /// <c>DownloadSettlement.RequeueFresh</c>, cleared when the file lands or is reset (sweep 2 #2).</summary>
    public bool RefetchRequested { get; set; }

    /// <summary>Back to never-touched (owner, 09-13): no local facts, no download history, no verify stamp, no
    /// carried strikes. What GOG told us about the file (Version, ExpectedMd5, ExpectedSizeBytes,
    /// ResolvedFileName) stays: those are facts about the file, not about our copy. THE one reset; every
    /// path that drops a file's local copy calls it, so no site forgets a field (four hand-rolled copies
    /// had drifted before sweep 2).</summary>
    public void ResetLocal()
    {
        State = FileState.NotBackedUp;
        RootId = null; LocalRelativePath = null; LocalSizeBytes = null;
        HasPartial = false; PartialBytes = null; PartialRootId = null;
        DownloadedAt = null; LastVerifiedAt = null; FailedAttempts = 0;
        RefetchRequested = false;
    }

    /// <summary>Accounts entitled to this FILE (ids; "" = primary). Per-file because entitlements differ per
    /// file; a gap can be served by any authenticated owner. Empty lists are seeded from the item's AccountId
    /// by the load-time migration.</summary>
    public List<string> OwnerIds { get; set; } = new();

    /// <summary>GOG's verbatim refusal reason when <see cref="FileState.Unavailable"/> is set; surfaced in
    /// Health. Null in every other state.</summary>
    public string? UnavailableReason { get; set; }

    /// <summary>Forward-compat: fields written by a newer build round-trip here instead of being dropped.</summary>
    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }
}
