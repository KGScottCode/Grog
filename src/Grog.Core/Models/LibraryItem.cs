// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Threading;
using System.Text.Json.Serialization;

namespace Grog.Core.Models;

/// <summary>A game (or movie) owned in the user's GOG library.</summary>
public sealed class LibraryItem
{
    public long GogId { get; set; }                 // GOG product id (library id, embed.gog.com scope)
    public string Title { get; set; } = "";
    public string Slug { get; set; } = "";          // url-safe name, used for folder naming
    public string ImageUrl { get; set; } = "";
    public bool IsMovie { get; set; }
    /// <summary>When the product entered the library, from GOG's order history. Null when unknown --
    /// see <see cref="NoPurchaseRecord"/> for the difference between "not asked yet" and "asked, not there".</summary>
    public DateTimeOffset? DateAcquired { get; set; }

    /// <summary>(Purchase dates 09-09) A COMPLETE walk of GOG's order history did not mention this product,
    /// so it has no acquisition date and asking again cannot change that. Measured: 53 of one reference
    /// library, and the causes are structural rather than transient -- Connect imports, DLC bundled under a
    /// base game, pre-order grants. This is the flag that lets the top-up pass go quiet: without it every
    /// scan re-asks about products whose answer is already known and final. A manifest without the field
    /// reads as false ("not asked yet"), which costs one backfill and then settles.</summary>
    public bool NoPurchaseRecord { get; set; }
    public DateTimeOffset LastApiRefresh { get; set; }
    public string? SerialKey { get; set; }          // cdKey when present
    public List<GameFile> Files { get; set; } = new();
    /// <summary>Retained previous builds (opt-in `KeepOldVersions`), archived under `Old Versions/&lt;slug&gt;/`.
    /// Kept separate from <see cref="Files"/> so they are auto-excluded from completeness, scope, counts and
    /// the queue; disk-aware consumers (composition, capacity, move) read this list explicitly.</summary>
    public List<GameFile> OldVersionFiles { get; set; } = new();
    public List<LibraryItem> Dlcs { get; set; } = new();

    /// <summary>What kind of product this is (game / movie / pack / dlc / mod). Derived during sync.</summary>
    public ProductType Type { get; set; } = ProductType.Game;

    /// <summary>For pack children and DLC: the slug of the parent product they belong to
    /// (from GOG's getFilteredProducts `url` field). Empty for standalone products.</summary>
    public string ParentSlug { get; set; } = "";

    /// <summary>Category text from GOG (e.g. "Role-playing", "Strategy").</summary>
    public string Category { get; set; } = "";

    /// <summary>FULL-LIBRARY status: the game measured against everything GOG offers, scope-blind; restamped
    /// every sync via BackupScope.StatusOf. Its only consumers are ChangesService, CLI listings and
    /// PreservationService -- scoped status is derived at read time and view filtering is HiddenPolicy.</summary>
    public BackupStatus Status { get; set; } = BackupStatus.Unknown;

    /// <summary>(New items 09-09) NEW to this library. Set when a scan ADDS an item that was not in the
    /// manifest before -- never on the very first scan of an empty library, which is the baseline (09-19).
    /// Cleared when the item becomes fully backed up in scope, or by an explicit "Clear New".
    /// Item level only; a new file on an owned item is Not Backed Up, not New. A manifest without the
    /// field reads as false, which is exactly right: an existing library is not new.</summary>
    public bool IsNew { get; set; }

    /// <summary>True when a sync no longer sees this product in the GOG account (delisted / removed
    /// from sale but still owned). Never deleted -- this is exactly what a backup tool must preserve.</summary>
    public bool IsDelisted { get; set; }

    // --- multi-account ---
    /// <summary>Serialization-only: the account this item was first synced from, kept solely so a pre-owners
    /// manifest can seed <see cref="GameFile.OwnerIds"/> at load. Real ownership is per-file.</summary>
    public string AccountId { get; set; } = "";

    /// <summary>Every account that owns any file of this game -- the union of the files' owner lists.
    /// Derived, never persisted at game level.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> OwnerIds
    {
        get
        {
            var set = new List<string>();
            foreach (var f in Files)
                foreach (var o in f.OwnerIds)
                    if (!set.Contains(o)) set.Add(o);
            if (set.Count == 0) set.Add(AccountId);   // pre-migration safety: attribution is still an owner
            return set;
        }
    }

    // --- useful GOG API fields (all from api.gog.com/products/{id}, captured at sync) ---
    /// <summary>GOG release date when supplied.</summary>
    public DateTimeOffset? ReleaseDate { get; set; }

    /// <summary>GOG's own game_type string ("game", "dlc", ...). Distinct from the derived Type;
    /// kept raw for reference.</summary>
    public string GameType { get; set; } = "";

    /// <summary>True when GOG marks the product as actively in development (still being patched).
    /// Preservation-relevant: actively-developed games change more, so they warrant re-checking.</summary>
    public bool InDevelopment { get; set; }

    /// <summary>OS availability per GOG's content_system_compatibility (win/mac/linux).</summary>
    public bool SupportsWindows { get; set; }
    public bool SupportsMac { get; set; }
    public bool SupportsLinux { get; set; }

    /// <summary>Language codes/names GOG lists for the product.</summary>
    public List<string> Languages { get; set; } = new();

    /// <summary>Age ratings by board ("pegi"/"esrb"/"usk"/"br" -> minimum age), from GOG's v2 per-game
    /// response at sync. The MAX across present boards is the effective minimum age for the content filter.</summary>
    public Dictionary<string, int> AgeRatings { get; set; } = new();

    /// <summary>Deep link to the product's GOG store page ("open on GOG").</summary>
    public string StorePageUrl { get; set; } = "";

    /// <summary>True if this game has GOG cloud saves (dev-enabled). Set when the cloud-save
    /// list is consulted. Null = not yet checked.</summary>
    public bool? HasCloudSaves { get; set; }

    /// <summary>Galaxy client id for this game, needed to reach its cloud saves. Empty
    /// until resolved from GOG's cloud-saves listing.</summary>
    public string CloudClientId { get; set; } = "";

    /// <summary>Total size of this game's cloud-save container in bytes (from the v2 containers list).</summary>
    public long CloudSaveSize { get; set; }

    /// <summary>Current file count in the cloud container (from the v2 list). Part of the change-detection
    /// fingerprint: a save added/removed changes this even when the total size happens to be unchanged.</summary>
    public int CloudSaveFiles { get; set; }

    /// <summary>When GOG last changed this container, if the API reports it. The PRIMARY change signal --
    /// size can stay identical when a same-size save is overwritten, so size alone silently misses new saves.</summary>
    public DateTimeOffset? CloudLatestChangeUtc { get; set; }

    /// <summary>V2 container space id (addresses the container in the v2 cloud-storage API).</summary>
    public string CloudSpaceId { get; set; } = "";

    /// <summary>When this game's cloud saves were last backed up locally (null = never).</summary>
    public DateTimeOffset? CloudLastBackup { get; set; }

    /// <summary>How many local saves are kept for this game. The JSON name is pinned to the original spelling
    /// -- a rename would read existing manifests back as zero.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("CloudSnapshotCount")]
    public int CloudLocalSaveCount { get; set; }

    /// <summary>The container size (bytes) at the last local backup. Part of the fingerprint compared against
    /// the live container to tell "up to date" from "update available".</summary>
    public long CloudBackedUpSize { get; set; }

    /// <summary>File count captured at the last COMPLETE snapshot. If a snapshot only wrote some of the files,
    /// this stays below CloudSaveFiles so the row keeps reading "update available" until a full backup lands.</summary>
    public int CloudBackedUpFiles { get; set; }

    /// <summary>The container change-timestamp captured at the last snapshot. Compared against the live
    /// CloudLatestChangeUtc: a newer server timestamp means saves changed since we backed up.</summary>
    public DateTimeOffset? CloudBackedUpChangeUtc { get; set; }

    /// <summary>Per-account cloud-save state (saves are per-account facts, never merged), in account
    /// registration order. The single-slot Cloud* fields above mirror the first entry (HasCloudSaves = any);
    /// the reconciler owns both, so nothing else may write the mirror directly.</summary>
    public List<CloudAccountSave> CloudByAccount { get; set; } = new();

    /// <summary>Forward-compat: fields written by a newer build round-trip here instead of being dropped.</summary>
    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }
}
