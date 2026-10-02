// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Grog.Core.Models;

namespace Grog.Core.Manifest;

/// <summary>Grog's source of truth about the library and local backup state: one human-readable JSON file
/// serialized directly from this graph. Saves are atomic (temp + rename) with a .bak kept every save.
/// Entries delisted by GOG are flagged, never removed -- delisted content is what a backup must remember.</summary>
public sealed class LibraryManifest
{
    // Initializer MUST be the current version: a fresh manifest saved at an old number would be re-"migrated"
    // on next load, clobbering new-field defaults (the v6 derive would overwrite ExtrasLayout from the bool).
    /// <summary>The schema this build writes. A file with a HIGHER version came from a newer Grog: refused at
    /// load (never "migrated" downward by ignorance), see <see cref="ManifestTooNewException"/>.</summary>
    public const int CurrentSchemaVersion = 6;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public DateTimeOffset? LastSyncCompleted { get; set; }
    private List<LibraryItem> _items = new();
    public List<LibraryItem> Items
    {
        get => _items;
        set { _items = value ?? new(); ItemsChanged(); }
    }

    // ItemById cache: id -> item, rebuilt when the list's Count or the mutation counter moved since it was built.
    // Items is a public List callers mutate directly, so Count catches Add/Remove/Clear and ItemsChanged() is
    // the hook for a same-Count replacement (mutators in Core call it; a stale hit is still verified by id).
    [JsonIgnore] private Dictionary<long, LibraryItem>? _byId;
    [JsonIgnore] private int _byIdCount = -1;
    [JsonIgnore] private int _byIdVersion = -1;
    [JsonIgnore] private int _itemsVersion;

    /// <summary>Tell the manifest Items was mutated in a way Count cannot show (an item replaced in place).</summary>
    public void ItemsChanged() => Interlocked.Increment(ref _itemsVersion);

    /// <summary>Keep the build an update replaces, under "Old Versions/". A LIBRARY fact, not an app
    /// preference: it travels with the backup so a cron `grog backup` honours it too (it silently overwrote
    /// superseded builds while this lived in the App's settings file; 09-02). The App migrates its old
    /// setting in once, one-way.</summary>
    public bool KeepOldVersions { get; set; }

    /// <summary>Worker count, bandwidth cap, cloud-save retention: library facts both hosts read (S2.3).</summary>
    public TransferSettings Transfer { get; set; } = new();

    /// <summary>The item for an id, or null. The one lookup every caller uses.</summary>
    public LibraryItem? ItemById(long gogId)
    {
        var items = _items;
        var map = _byId;
        if (map is null || _byIdCount != items.Count || _byIdVersion != _itemsVersion)
        {
            int version = _itemsVersion;
            map = new Dictionary<long, LibraryItem>(items.Count);
            // First occurrence wins, matching the FirstOrDefault this replaces.
            foreach (var i in items) map.TryAdd(i.GogId, i);
            _byId = map;
            _byIdCount = items.Count;
            _byIdVersion = version;
        }
        if (map.TryGetValue(gogId, out var hit) && hit.GogId == gogId) return hit;
        // A miss falls back to the scan: an unknown id is a legitimate miss, and a same-Count replacement
        // nobody flagged is caught here instead of answering null. A stale find refreshes the cache.
        var found = items.FirstOrDefault(i => i.GogId == gogId);
        if (found is not null) ItemsChanged();
        return found;
    }

    /// <summary>The CLI's game query: an exact GOG id, else the first title containing the text
    /// (case-insensitive). Six verbs each spelled this out before 09-02.</summary>
    public LibraryItem? FindItem(string query)
        => Items.FirstOrDefault(i => i.GogId.ToString() == query
                                  || (i.Title?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));
    public List<SyncLogEntry> SyncLog { get; set; } = new();   // capped, newest first

    /// <summary>Owned products the last scan found nothing downloadable for, with their classification
    /// signals; kept (not as Items) to explain the owned-vs-backed-up count gap. Rebuilt wholesale each scan.</summary>
    public List<ExcludedProduct> Excluded { get; set; } = new();

    /// <summary>Cloud Saves page: account sections the user has collapsed (view state, persisted like
    /// the other per-view states; empty = all expanded).</summary>
    public List<string> CloudCollapsedAccounts { get; set; } = new();
    public Dictionary<string, string> Settings { get; set; } = new();

    /// <summary>Backup volumes. One "Primary" root exists by default; more are opt-in.</summary>
    public List<Models.BackupRoot> Roots { get; set; } = new();

    /// <summary>Id of the primary root -- the default destination for content.</summary>
    public string? PrimaryRootId { get; set; }

    /// <summary>The root a file's bytes live on: its own RootId, or the primary when the record predates roots.</summary>
    public string? EffectiveRootId(Models.GameFile f) => f.RootId ?? PrimaryRootId;

    /// <summary>Optional override for where cloud-save archives land. Empty = the default
    /// (&lt;primary root&gt;/Cloud Saves). When set, it's an absolute folder that all cloud-save
    /// snapshots go under, resolved by BackupLayout for both the CLI and GUI.</summary>
    public string CloudSavesFolder { get; set; } = "";

    /// <summary>How files are routed across roots (defaults to everything -> primary).</summary>
    public Models.RoutingPolicy Routing { get; set; } = new();

    /// <summary>The persisted download queue: single source of truth for what is queued and whether it is
    /// paused; survives restart, the engine executes from it, every UI surface derives from it. The move
    /// queue is the reorg journal (its own store), so only the download queue lives here.</summary>
    public Download.DownloadQueue Downloads { get; set; } = new();

    /// <summary>LEGACY mirror of <see cref="ExtrasLayout"/>, kept so an older build opening this library
    /// still reads a sane layout (true = separate tree by game, false = by type; WithGame maps to true).
    /// JsonManifestStore re-derives it on every save; nothing else may READ it -- read ExtrasLayout.</summary>
    public bool ExtrasInsideGame { get; set; } = true;

    /// <summary>Where extras live: WithGame (default for new libraries) = <c>Games/&lt;game&gt;/Extras/</c>
    /// inside the game folder; the Separate* values are the classic top-level Extras/ tree. A property of the
    /// backup folder, so it travels with the drive. Pre-v6 manifests derive this from ExtrasInsideGame on
    /// load (JsonManifestStore.MigrateSchema) so an existing library never silently changes shape.</summary>
    public Models.ExtrasPlacement ExtrasLayout { get; set; } = Models.ExtrasPlacement.WithGame;

    /// <summary>Sets the layout AND keeps the legacy bool mirror consistent for downgrade reads.</summary>
    public void SetExtrasLayout(Models.ExtrasPlacement value)
    {
        ExtrasLayout = value;
        ExtrasInsideGame = value != Models.ExtrasPlacement.SeparateByType;
    }

    /// <summary>The user's backup scope (games/extras, languages, platforms) -- a property of the BACKUP, not
    /// the machine, so the CLI on any OS honors what the GUI configured. Null = never seeded: the GUI seeds
    /// it from per-machine AppSettings on first load; the CLI treats null as "no narrowing".</summary>
    public Sync.ScopeSettings? Scope { get; set; }

    /// <summary>GOG accounts this library is synced from. Empty/one entry = single-account,
    /// the common case. Items carry an AccountId tag; all accounts merge into this one catalog.</summary>
    public List<Models.GrogAccount> Accounts { get; set; } = new();

    /// <summary>Automatic-backup schedule. Off by default; when on, an in-app timer runs a
    /// Sync-backup on the configured cadence while Grog is open or in the tray.</summary>
    public Scheduling.BackupSchedule Schedule { get; set; } = new();

    /// <summary>Forward-compat: fields written by a NEWER build that this build doesn't know are captured
    /// here and written back on save, so opening a library in an older Grog never silently strips data.</summary>
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class SyncLogEntry
{
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public int GamesSeen { get; set; }
    public int Changes { get; set; }
    public string Notes { get; set; } = "";
}

/// <summary>An owned product with nothing to download, kept only to explain the item-count gap. Carries the
/// raw classification signals rather than a verdict -- promo claims and bundle shells are indistinguishable
/// in GOG's API.</summary>
public sealed class ExcludedProduct
{
    public long GogId { get; set; }
    public string Title { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Category { get; set; } = "";
    /// <summary>The taxonomy verdict at scan time (Dlc, Pack, Mod, Movie).</summary>
    public Models.ProductType Type { get; set; }
    /// <summary>This product's row names a DIFFERENT product as its parent: it is child content.</summary>
    public bool IsChildOfPack { get; set; }
    /// <summary>Some other owned product names THIS one as its parent: it is a bundle/pack shell.</summary>
    public bool ReferencedAsParent { get; set; }
    public bool IsMovie { get; set; }
    public int DlcCount { get; set; }
    /// <summary>Non-empty = GOG FAILED to serve this product's details this scan (HTTP error), as opposed
    /// to the deliberate empty/404 "nothing to download" answer. Possibly transient: the excluded list is
    /// rebuilt every scan, so the product returns to the library the moment GOG serves it again.</summary>
    public string UnavailableReason { get; set; } = "";
}
