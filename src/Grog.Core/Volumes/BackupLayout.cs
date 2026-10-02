// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Storage;
using Grog.Core.Verify;

namespace Grog.Core.Volumes;

/// <summary>
/// Root-aware path resolution. Every consumer that touches a file on disk (download, verify, import,
/// relocate) goes through this instead of raw Path.Combine, so multi-volume, offline drives, and
/// drive-letter changes are handled in exactly one place.
/// </summary>
public interface IBackupLayout
{
    /// <summary>Resolve the absolute path for a file, or null if its root is offline/unknown.</summary>
    string? ResolvePath(GameFile file);

    /// <summary>The directory a file should live in for a given game + kind on its target root, used when
    /// downloading (before the file has a RootId). Null if the target root is offline. <paramref name="file"/>
    /// is only needed for the type-grouped extras layout.</summary>
    string? ResolveTargetDir(long gameGogId, string gameSlug, FileKind kind, out string rootId, GameFile? file = null);

    /// <summary>The folder shape below a root ("Games/witcher", "Extras/witcher/Soundtracks"), independent of
    /// device -- used by moves, where the user picks the root by hand. Extras always live under Extras/; the
    /// game-first vs type-first choice only reorders the two folders below it.</summary>
    string SubPathFor(string gameSlug, FileKind kind, GameFile? file = null, long gogId = 0, string? targetRootId = null);

    /// <summary>The directory a game's cloud-save snapshots live in (<c>&lt;root&gt;/Cloud Saves/&lt;slug&gt;</c>),
    /// honoring the CloudSaves role's root. Null if that root is offline.</summary>
    string? ResolveCloudDir(string gameSlug, out string rootId);

    /// <summary>Is the given root currently available (marker found, path bound)?</summary>
    bool IsOnline(string? rootId);

    /// <summary>The absolute bound path of a root, or null if offline/unknown.</summary>
    string? RootPath(string? rootId);

    /// <summary>Every online root's bound path. The engine uses it to find a partial that an earlier run
    /// started on a device the file is no longer routed to.</summary>
    IReadOnlyCollection<string> OnlineRootPaths { get; }
}

/// <summary>
/// Default layout backed by the manifest's roots + routing policy. Construction scans volumes, binding each
/// root's current path and marking roots online/offline; a brand-new manifest gets a single "Primary" root.
/// </summary>
public sealed class BackupLayout : IBackupLayout
{
    private readonly LibraryManifest _m;
    private readonly Dictionary<string, string> _bound = new();  // rootId -> absolute path (online only)

    public BackupLayout(LibraryManifest manifest, string primaryPath)
    {
        UiThreadGuard.NotOnUi("a backup-root folder probe (BackupLayout)");
        _m = manifest;
        EnsurePrimary(primaryPath);
        ScanVolumes(primaryPath);
    }

    /// <summary>Guarantees a primary root exists (first run). Hosts that build layouts on worker threads call
    /// this on their UI thread first (UI-thread sweep 09-06): it is the one manifest STRUCTURE change the
    /// constructor can make, and a Roots.Add under the UI's enumeration is a crash.</summary>
    public static void EnsurePrimary(LibraryManifest manifest, string primaryPath) => new BackupLayout(manifest, primaryPath, ensureOnly: true);
    private BackupLayout(LibraryManifest manifest, string primaryPath, bool ensureOnly) { _m = manifest; EnsurePrimary(primaryPath); }

    private void EnsurePrimary(string primaryPath)
    {
        // No path given: never invent a primary or scaffold folders (that would create junk at the CWD).
        // A caller with no configured location must fail fast, not have one guessed here.
        if (string.IsNullOrWhiteSpace(primaryPath)) return;
        if (_m.Roots.Count == 0 || _m.PrimaryRootId is null)
        {
            var root = new BackupRoot { Label = "Primary", PathHint = primaryPath };
            // (First-run adopt 09-10) The DEFAULT primary owes an import just like a folder the user nominates
            // through "Add location". It is the one root nobody is asked about, so it was the one root never
            // walked: games already sitting in the default path before Grog's first run were invisible, every
            // one of them read Not Backed Up, and a first backup re-downloaded a library that was already
            // there. Marking it here covers both hosts, because this is where the default root is born.
            // The flag is only a promise to LOOK; an empty folder adopts nothing and costs one walk.
            PendingImports.Mark(root);
            _m.Roots.Add(root);
            _m.PrimaryRootId = root.Id;
            // Route all roles at the primary by default.
            _m.Routing.SetRole(ContentRole.Games, root.Id);
            _m.Routing.SetRole(ContentRole.Extras, root.Id);
            _m.Routing.SetRole(ContentRole.CloudSaves, root.Id);
        }
    }

    /// <summary>Creates the top-level category folders under the primary root. Deliberately NOT called from
    /// the constructor: folders must only appear after the user confirms or picks a location.</summary>
    public void ScaffoldCategoryFolders()
    {
        if (_m.PrimaryRootId is { } id && RootPath(id) is { Length: > 0 } p) ScaffoldCategoryFolders(p);
    }

    /// <summary>Top-level Extras/ only exists in the separate-tree layouts; with extras inside each game
    /// folder (WithGame, the default) scaffolding it would create a permanently empty folder.</summary>
    private void ScaffoldCategoryFolders(string rootPath)
    {
        try { Directory.CreateDirectory(Path.Combine(rootPath, "Games")); } catch { }
        try { Directory.CreateDirectory(Path.Combine(rootPath, "Cloud Saves")); } catch { }
        if (_m.ExtrasLayout != ExtrasPlacement.WithGame)
            try { Directory.CreateDirectory(Path.Combine(rootPath, "Extras")); } catch { }
    }

    private void ScanVolumes(string primaryPath)
    {
        // Over a copy: the App builds layouts on worker threads now (UI-thread sweep 09-06) while the UI can
        // add or remove a root; the writes below are per-root field updates (benign), the enumeration is not.
        foreach (var root in _m.Roots.ToArray())
        {
            // A detached drive is on the shelf by the user's word: never bound, never written, whatever is at
            // its old path (09-13). It stays Detached until Reattach says otherwise.
            if (root.State == RootState.Detached) continue;
            // The PRIMARY's path is the manifest's fact (root.PathHint): Change Folder, `--primary` and a
            // promotion all write it there. The caller's path is only the seed for a manifest that has none
            // yet (first run). Before 09-04 the caller's path won and was written BACK into PathHint, so a host
            // whose own setting lagged (the App after a promotion, or after a CLI --primary) silently dragged
            // the primary root onto the wrong folder. One fact, in one place.
            // The fact must be ON DISK to win: a portable install whose drive letter changed has a dead
            // PathHint and a live caller path (ChosenRoot re-anchored through the {portable} token), and the
            // caller then heals the hint. Neither present: keep the hint (offline), never invent a folder.
            if (root.Id == _m.PrimaryRootId)
            {
                bool hintLive = !string.IsNullOrWhiteSpace(root.PathHint) && Directory.Exists(root.PathHint);
                var path = hintLive ? root.PathHint
                         : !string.IsNullOrWhiteSpace(primaryPath) ? primaryPath
                         : PortableFallback(root) ?? root.PathHint;
                if (!string.IsNullOrEmpty(path)) Bind(root, path);
                else root.State = RootState.Offline;
                continue;
            }

            // Non-primary: bind if its last-known path is present on disk, else its portable token (a secondary on
            // the same re-lettered stick), else mark offline. A drive that came back under another letter is matched
            // by its files when the user adds that folder (ImportRun.MatchExistingRoot) and re-pointed there.
            if (!string.IsNullOrEmpty(root.PathHint) && Directory.Exists(root.PathHint))
                Bind(root, root.PathHint);
            else if (PortableFallback(root) is { } portable)
                Bind(root, portable);
            else
                root.State = RootState.Offline;
        }
    }

    private void Bind(BackupRoot root, string path)
    {
        var full = Path.GetFullPath(path);
        _bound[root.Id] = full;
        root.PathHint = full;
        // Under a portable install the hint is also kept as a {portable} token, so a re-lettered stick re-binds.
        var stored = GrogPaths.StorePath(full);
        root.PortablePath = stored.StartsWith(GrogPaths.PortableToken, StringComparison.Ordinal) ? stored : null;
        root.LastSeen = DateTimeOffset.UtcNow;
        if (root.State is RootState.Offline or RootState.Online) root.State = RootState.Online;
    }

    /// <summary>A dead hint's portable token resolved against the install folder as mounted now, when that folder exists.</summary>
    private static string? PortableFallback(BackupRoot root)
    {
        if (string.IsNullOrEmpty(root.PortablePath)) return null;
        var resolved = GrogPaths.ResolvePath(root.PortablePath);
        return !resolved.StartsWith(GrogPaths.PortableToken, StringComparison.Ordinal) && Directory.Exists(resolved) ? resolved : null;
    }

    public bool IsOnline(string? rootId) => rootId is not null && _bound.ContainsKey(rootId);
    public IReadOnlyCollection<string> OnlineRootPaths => _bound.Values;

    public string? RootPath(string? rootId) =>
        rootId is not null && _bound.TryGetValue(rootId, out var p) ? p : null;

    public string? ResolvePath(GameFile file)
    {
        var rootId = file.RootId ?? _m.PrimaryRootId;
        var root = RootPath(rootId);
        if (root is null || string.IsNullOrEmpty(file.LocalRelativePath)) return null;
        // Manifests store relative paths with '/' (portable); convert to the OS-native separator so the
        // resolved path is consistent for string comparisons (orphan/dedup checks) and cross-platform.
        var native = file.LocalRelativePath
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(root, native);
    }

    public string? ResolveTargetDir(long gameGogId, string gameSlug, FileKind kind, out string rootId, GameFile? file = null)
    {
        rootId = _m.Routing.ResolveRootId(gameGogId, kind, _m.PrimaryRootId!, _m.ExtrasLayout);
        var root = RootPath(rootId);
        if (root is null) return null;   // target root offline

        // Games sit at <root>/Games/<slug>; extras under <root>/Extras/ shaped by SubPathFor.
        return Path.Combine(root, SubPathFor(gameSlug, kind, file, gameGogId, rootId));
    }

    public string SubPathFor(string gameSlug, FileKind kind, GameFile? file = null, long gogId = 0, string? targetRootId = null)
    {
        var role = RoutingPolicy.RoleOf(kind);
        if (role != ContentRole.Extras) return Path.Combine(ContentRoleFolders.For(role), gameSlug);
        // WithGame (default): flat Extras/ INSIDE the game folder -- Games/<slug>/Extras/<file>, no buckets.
        if (_m.ExtrasLayout == ExtrasPlacement.WithGame)
            return Path.Combine(ContentRoleFolders.For(ContentRole.Games), gameSlug, "Extras");
        // Separate tree: extras live under top-level Extras/; the sub-choice only orders the two folders
        // below: game first -> Extras/<slug>/<bucket>, type first -> Extras/<bucket>/<slug>.
        var extras = ContentRoleFolders.For(role);
        var bucket = Sync.ExtraClassifier.FolderFor(Sync.ExtraClassifier.Classify(file?.Name, file?.ExtraType));
        return _m.ExtrasLayout == ExtrasPlacement.SeparateByGame
            ? Path.Combine(extras, gameSlug, bucket)
            : Path.Combine(extras, bucket, gameSlug);
    }

    /// <summary>Base folder cloud-save archives land in (no game slug): the CloudSavesFolder override, else
    /// &lt;root&gt;/Cloud Saves via the CloudSaves role. One resolver so CLI, GUI and reconcile agree.</summary>
    public string? ResolveCloudBaseDir(out string rootId)
    {
        rootId = _m.Routing.RoleRoots.TryGetValue(ContentRole.CloudSaves, out var r) && !string.IsNullOrEmpty(r)
            ? r : _m.PrimaryRootId!;
        if (!string.IsNullOrEmpty(_m.CloudSavesFolder)) return _m.CloudSavesFolder;   // explicit override wins
        var root = RootPath(rootId);
        return root is null ? null : Path.Combine(root, ContentRoleFolders.For(ContentRole.CloudSaves));
    }

    public string? ResolveCloudDir(string gameSlug, out string rootId)
    {
        var baseDir = ResolveCloudBaseDir(out rootId);
        return baseDir is null ? null : Path.Combine(baseDir, gameSlug);
    }
}
