// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;

namespace Grog.Core.Models;

public sealed class BackupRoot
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

    public string Label { get; set; } = "";

    public string PathHint { get; set; } = "";

    public string? PortablePath { get; set; }

    public RootState State { get; set; } = RootState.Online;

    public DateTimeOffset? LastSeen { get; set; }

    public bool Removable { get; set; }

    public List<string> PriorIds { get; set; } = new();

    public bool PendingImport { get; set; }
}

public enum RootState { Online = 0, Offline = 1, Lost = 2, Replaced = 3, Detached = 4 }

public enum ContentRole { Games = 0, Extras = 1, CloudSaves = 2 }

public enum ExtrasPlacement { WithGame = 0, SeparateByGame = 1, SeparateByType = 2 }

public static class ContentRoleFolders
{
    public static string For(ContentRole role) => role switch
    {
        ContentRole.Extras => "Extras",
        ContentRole.CloudSaves => "Cloud Saves",
        _ => "Games",
    };

    public static readonly string[] All = { "Games", "Extras", "Cloud Saves" };
}

public enum RoutingMode { Overflow = 0, ByContent = 1, Manual = 2 }

public sealed class RoutingPolicy
{
    public RoutingMode Mode { get; set; } = RoutingMode.Overflow;

    public Dictionary<ContentRole, string> RoleRoots { get; set; } = new();

    public static ContentRole RoleOf(FileKind kind) => kind switch
    {
        FileKind.Extra => ContentRole.Extras,
        _ => ContentRole.Games,   // Installer, Patch, LanguagePack, DlcInstaller
    };

    public string ResolveRootId(long gameGogId, FileKind kind, string primaryRootId, ExtrasPlacement layout)
    {
        var role = RoleOf(kind);
        if (role == ContentRole.Extras && layout == ExtrasPlacement.WithGame) role = ContentRole.Games;
        return RoleRootOrPrimary(role, primaryRootId)!;
    }

    public string? RoleRootOrPrimary(ContentRole role, string? primaryRootId)
        => RoleRoots.TryGetValue(role, out var roleRoot) && !string.IsNullOrEmpty(roleRoot) ? roleRoot : primaryRootId;

    public void SetRole(ContentRole role, string rootId) => RoleRoots[role] = rootId;
}
