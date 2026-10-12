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

public interface IBackupLayout
{
    string? ResolvePath(GameFile file);

    string? ResolveTargetDir(long gameGogId, string gameSlug, FileKind kind, out string rootId, GameFile? file = null);

    string SubPathFor(string gameSlug, FileKind kind, GameFile? file = null, long gogId = 0, string? targetRootId = null);

    string? ResolveCloudDir(string gameSlug, out string rootId);

    bool IsOnline(string? rootId);

    string? RootPath(string? rootId);

    IReadOnlyCollection<string> OnlineRootPaths { get; }
}

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

    public static void EnsurePrimary(LibraryManifest manifest, string primaryPath) => new BackupLayout(manifest, primaryPath, ensureOnly: true);
    private BackupLayout(LibraryManifest manifest, string primaryPath, bool ensureOnly) { _m = manifest; EnsurePrimary(primaryPath); }

    private void EnsurePrimary(string primaryPath)
    {
        if (string.IsNullOrWhiteSpace(primaryPath)) return;
        if (_m.Roots.Count == 0 || _m.PrimaryRootId is null)
        {
            var root = new BackupRoot { Label = "Primary", PathHint = primaryPath };
            PendingImports.Mark(root);
            _m.Roots.Add(root);
            _m.PrimaryRootId = root.Id;
            _m.Routing.SetRole(ContentRole.Games, root.Id);
            _m.Routing.SetRole(ContentRole.Extras, root.Id);
            _m.Routing.SetRole(ContentRole.CloudSaves, root.Id);
        }
    }

    public void ScaffoldCategoryFolders()
    {
        if (_m.PrimaryRootId is { } id && RootPath(id) is { Length: > 0 } p) ScaffoldCategoryFolders(p);
    }

    private void ScaffoldCategoryFolders(string rootPath)
    {
        try { Directory.CreateDirectory(Path.Combine(rootPath, "Games")); } catch { }
        try { Directory.CreateDirectory(Path.Combine(rootPath, "Cloud Saves")); } catch { }
        if (_m.ExtrasLayout != ExtrasPlacement.WithGame)
            try { Directory.CreateDirectory(Path.Combine(rootPath, "Extras")); } catch { }
    }

    private void ScanVolumes(string primaryPath)
    {
        foreach (var root in _m.Roots.ToArray())
        {
            if (root.State == RootState.Detached) continue;
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
        var stored = GrogPaths.StorePath(full);
        root.PortablePath = stored.StartsWith(GrogPaths.PortableToken, StringComparison.Ordinal) ? stored : null;
        root.LastSeen = DateTimeOffset.UtcNow;
        if (root.State is RootState.Offline or RootState.Online) root.State = RootState.Online;
    }

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

        return Path.Combine(root, SubPathFor(gameSlug, kind, file, gameGogId, rootId));
    }

    public string SubPathFor(string gameSlug, FileKind kind, GameFile? file = null, long gogId = 0, string? targetRootId = null)
    {
        var role = RoutingPolicy.RoleOf(kind);
        if (role != ContentRole.Extras) return Path.Combine(ContentRoleFolders.For(role), gameSlug);
        if (_m.ExtrasLayout == ExtrasPlacement.WithGame)
            return Path.Combine(ContentRoleFolders.For(ContentRole.Games), gameSlug, "Extras");
        var extras = ContentRoleFolders.For(role);
        var bucket = Sync.ExtraClassifier.FolderFor(Sync.ExtraClassifier.Classify(file?.Name, file?.ExtraType));
        return _m.ExtrasLayout == ExtrasPlacement.SeparateByGame
            ? Path.Combine(extras, gameSlug, bucket)
            : Path.Combine(extras, bucket, gameSlug);
    }

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
