// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Threading;
using System.Text.Json.Serialization;

namespace Grog.Core.Models;

public sealed class LibraryItem
{
    public long GogId { get; set; }                 // GOG product id (library id, embed.gog.com scope)
    public string Title { get; set; } = "";
    public string Slug { get; set; } = "";          // url-safe name, used for folder naming
    public string ImageUrl { get; set; } = "";
    public bool IsMovie { get; set; }
    public DateTimeOffset? DateAcquired { get; set; }

    public bool NoPurchaseRecord { get; set; }
    public DateTimeOffset LastApiRefresh { get; set; }
    public string? SerialKey { get; set; }          // cdKey when present
    public List<GameFile> Files { get; set; } = new();
    public List<GameFile> OldVersionFiles { get; set; } = new();
    public List<LibraryItem> Dlcs { get; set; } = new();

    public ProductType Type { get; set; } = ProductType.Game;

    public string ParentSlug { get; set; } = "";

    public string Category { get; set; } = "";

    public BackupStatus Status { get; set; } = BackupStatus.Unknown;

    public bool IsNew { get; set; }

    public bool IsDelisted { get; set; }

    // --- multi-account ---
    public string AccountId { get; set; } = "";

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
    public DateTimeOffset? ReleaseDate { get; set; }

    public string GameType { get; set; } = "";

    public bool InDevelopment { get; set; }

    public bool SupportsWindows { get; set; }
    public bool SupportsMac { get; set; }
    public bool SupportsLinux { get; set; }

    public List<string> Languages { get; set; } = new();

    public Dictionary<string, int> AgeRatings { get; set; } = new();

    public string StorePageUrl { get; set; } = "";

    public bool? HasCloudSaves { get; set; }

    public string CloudClientId { get; set; } = "";

    public long CloudSaveSize { get; set; }

    public int CloudSaveFiles { get; set; }

    public DateTimeOffset? CloudLatestChangeUtc { get; set; }

    public string CloudSpaceId { get; set; } = "";

    public DateTimeOffset? CloudLastBackup { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("CloudSnapshotCount")]
    public int CloudLocalSaveCount { get; set; }

    public long CloudBackedUpSize { get; set; }

    public int CloudBackedUpFiles { get; set; }

    public DateTimeOffset? CloudBackedUpChangeUtc { get; set; }

    public List<CloudAccountSave> CloudByAccount { get; set; } = new();

    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }
}
