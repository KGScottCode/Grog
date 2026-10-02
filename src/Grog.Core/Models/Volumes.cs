// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;

namespace Grog.Core.Models;

/// <summary>
/// A logical backup volume ("drive"/"location"). Identity is the stable <see cref="Id"/>, kept in the profile only;
/// the backup folder itself carries nothing of Grog's. A drive back under another letter is matched by its files
/// when its folder is added again (ImportRun.MatchExistingRoot) and re-pointed.
/// </summary>
public sealed class BackupRoot
{
    /// <summary>Stable logical id. Never reused.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

    /// <summary>Human label the user picked ("Primary", "Archive", "USB Backup").</summary>
    public string Label { get; set; } = "";

    /// <summary>Last known filesystem path. A hint -- the marker is the source of truth.</summary>
    public string PathHint { get; set; } = "";

    /// <summary>The hint as a <c>{portable}/relative</c> token when it sits under a portable install, else null. The
    /// volume scan re-binds through it when the absolute hint died with a drive letter.</summary>
    public string? PortablePath { get; set; }

    /// <summary>Lifecycle state set by the volume scan; only user-declared Lost/Replaced persist as truth.</summary>
    public RootState State { get; set; } = RootState.Online;

    /// <summary>When this root's marker was last seen by a volume scan.</summary>
    public DateTimeOffset? LastSeen { get; set; }

    /// <summary>User-declared detachable device (USB/external): offline files count as safe-on-detached-drive,
    /// not missing, so rescan/verify never flags them for re-download.</summary>
    public bool Removable { get; set; }

    /// <summary>Prior marker ids for this logical root (drive replacements), newest last.</summary>
    public List<string> PriorIds { get; set; } = new();

    /// <summary>The user chose "Scan &amp; Add" before any library scan, so the import (matching files
    /// already in the folder to library games) could not run yet. Persisted so a restart before the first
    /// scan does not forget it; cleared once the import has run.</summary>
    public bool PendingImport { get; set; }
}

/// <summary>Runtime availability of a root: Online = marker found; Offline = marker absent (unplugged, not an
/// error); Lost = user-declared dead; Replaced = lost root superseded by a new drive; Detached (09-13) =
/// user put the drive on the shelf: its files stay recorded and count as backed up, it is never bound or
/// written to (even if its path is present), and it does not hold one of the two storage slots. A Reattach
/// makes it Offline again so the next scan binds it.</summary>
public enum RootState { Online = 0, Offline = 1, Lost = 2, Replaced = 3, Detached = 4 }

/// <summary>User-facing content roles files route by: Games = installable content (Installer, Patch,
/// DlcInstaller); Extras = archival goodies (FileKind.Extra, including a DLC's own).</summary>
public enum ContentRole { Games = 0, Extras = 1, CloudSaves = 2 }

/// <summary>Where extras live on disk. WithGame (default) = inside the game's own folder
/// (<c>Games/&lt;game&gt;/Extras/&lt;file&gt;</c>, flat) -- one folder carries everything a game owns, and
/// extras always route WITH the game (the Extras role root is ignored). SeparateByGame /
/// SeparateByType = the classic top-level <c>Extras/</c> tree, ordered game-first or type-first.</summary>
public enum ExtrasPlacement { WithGame = 0, SeparateByGame = 1, SeparateByType = 2 }

/// <summary>The top-level folder name each role writes into under a backup root
/// (<c>&lt;root&gt;/Games</c>, <c>/Extras</c>, <c>/Cloud Saves</c>).</summary>
public static class ContentRoleFolders
{
    public static string For(ContentRole role) => role switch
    {
        ContentRole.Extras => "Extras",
        ContentRole.CloudSaves => "Cloud Saves",
        _ => "Games",
    };

    /// <summary>All category folders, for scaffolding a new backup root.</summary>
    public static readonly string[] All = { "Games", "Extras", "Cloud Saves" };
}

/// <summary>How new content is spread across two+ devices. Overflow: fill primary, spill the rest.
/// ByContent: route Games/Extras to chosen devices, spilling when one fills. Manual: send everything to its
/// routed device and never auto-spill (the fit-check warns). With a single device the mode is moot.</summary>
public enum RoutingMode { Overflow = 0, ByContent = 1, Manual = 2 }

public sealed class RoutingPolicy
{
    /// <summary>Placement mode across devices; Overflow is the safe no-config default.</summary>
    public RoutingMode Mode { get; set; } = RoutingMode.Overflow;

    /// <summary>Root id used for a role when no more specific rule applies. Both default to primary.</summary>
    public Dictionary<ContentRole, string> RoleRoots { get; set; } = new();

    public static ContentRole RoleOf(FileKind kind) => kind switch
    {
        FileKind.Extra => ContentRole.Extras,
        _ => ContentRole.Games,   // Installer, Patch, LanguagePack, DlcInstaller
    };

    /// <summary>Resolves a file's target root id: the role default, then the primary fallback.
    /// The layout is REQUIRED because it changes the answer: with <see cref="ExtrasPlacement.WithGame"/> an
    /// extra lives inside its game's folder, so it routes by the Games role, never the Extras role.</summary>
    public string ResolveRootId(long gameGogId, FileKind kind, string primaryRootId, ExtrasPlacement layout)
    {
        var role = RoleOf(kind);
        if (role == ContentRole.Extras && layout == ExtrasPlacement.WithGame) role = ContentRole.Games;
        return RoleRootOrPrimary(role, primaryRootId)!;
    }

    /// <summary>THE reader of a role's root: the pinned root when one is set, else the primary. Four call sites
    /// spelled out the TryGetValue + IsNullOrEmpty + fallback by hand (sweep 2 refactor).</summary>
    public string? RoleRootOrPrimary(ContentRole role, string? primaryRootId)
        => RoleRoots.TryGetValue(role, out var roleRoot) && !string.IsNullOrEmpty(roleRoot) ? roleRoot : primaryRootId;

    public void SetRole(ContentRole role, string rootId) => RoleRoots[role] = rootId;
}
