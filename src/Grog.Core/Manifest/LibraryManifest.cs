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

public sealed class LibraryManifest
{
    public const int CurrentSchemaVersion = 6;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public DateTimeOffset? LastSyncCompleted { get; set; }
    private List<LibraryItem> _items = new();
    public List<LibraryItem> Items
    {
        get => _items;
        set { _items = value ?? new(); ItemsChanged(); }
    }

    [JsonIgnore] private Dictionary<long, LibraryItem>? _byId;
    [JsonIgnore] private int _byIdCount = -1;
    [JsonIgnore] private int _byIdVersion = -1;
    [JsonIgnore] private int _itemsVersion;

    public void ItemsChanged() => Interlocked.Increment(ref _itemsVersion);

    public bool KeepOldVersions { get; set; }

    public TransferSettings Transfer { get; set; } = new();

    public LibraryItem? ItemById(long gogId)
    {
        var items = _items;
        var map = _byId;

        if (map is null || _byIdCount != items.Count || _byIdVersion != _itemsVersion)
        {
            int version = _itemsVersion;
            map = new Dictionary<long, LibraryItem>(items.Count);
            foreach (var i in items) map.TryAdd(i.GogId, i);
            _byId = map;
            _byIdCount = items.Count;
            _byIdVersion = version;
        }
        if (map.TryGetValue(gogId, out var hit) && hit.GogId == gogId) return hit;

        var found = items.FirstOrDefault(i => i.GogId == gogId);

        if (found is not null) ItemsChanged();

        return found;
    }

    public LibraryItem? FindItem(string query)
        => Items.FirstOrDefault(i => i.GogId.ToString() == query
                                  || (i.Title?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));
    public List<SyncLogEntry> SyncLog { get; set; } = new();   // capped, newest first

    public List<ExcludedProduct> Excluded { get; set; } = new();

    public List<string> CloudCollapsedAccounts { get; set; } = new();
    public Dictionary<string, string> Settings { get; set; } = new();

    public List<Models.BackupRoot> Roots { get; set; } = new();

    public string? PrimaryRootId { get; set; }

    public string? EffectiveRootId(Models.GameFile f) => f.RootId ?? PrimaryRootId;

    public string CloudSavesFolder { get; set; } = "";

    public Models.RoutingPolicy Routing { get; set; } = new();

    public Download.DownloadQueue Downloads { get; set; } = new();

    public bool ExtrasInsideGame { get; set; } = true;

    public Models.ExtrasPlacement ExtrasLayout { get; set; } = Models.ExtrasPlacement.WithGame;

    public void SetExtrasLayout(Models.ExtrasPlacement value)
    {
        ExtrasLayout = value;
        ExtrasInsideGame = value != Models.ExtrasPlacement.SeparateByType;
    }

    public Sync.ScopeSettings? Scope { get; set; }

    public List<Models.GrogAccount> Accounts { get; set; } = new();

    public Scheduling.BackupSchedule Schedule { get; set; } = new();

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

public sealed class ExcludedProduct
{
    public long GogId { get; set; }
    public string Title { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Category { get; set; } = "";
    public Models.ProductType Type { get; set; }
    public bool IsChildOfPack { get; set; }
    public bool ReferencedAsParent { get; set; }
    public bool IsMovie { get; set; }
    public int DlcCount { get; set; }
    public string UnavailableReason { get; set; } = "";
}
