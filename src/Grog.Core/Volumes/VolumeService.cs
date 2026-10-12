// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Storage;

namespace Grog.Core.Volumes;

public sealed class VolumeService
{
    private readonly IManifestStore _store;
    public VolumeService(IManifestStore store, PendingWrites? pending = null) { _store = store; _pending = pending; }
    private readonly PendingWrites? _pending;

    private LibraryManifest M => _store.Current;

    // ---- roots -----------------------------------------------------------------------------

    public BackupRoot AddRoot(string path, string? label = null, bool removable = false)
    {
        path = Path.GetFullPath(path);
        var existing = M.Roots.FirstOrDefault(r =>
            !string.IsNullOrEmpty(r.PathHint) &&
            string.Equals(Path.GetFullPath(r.PathHint), path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            using (_store.Gate.Enter())
                if (existing.State == RootState.Detached)
                {
                    existing.State = RootState.Offline;   // LastSeen is the volume scan's to stamp (Bind)
                    if (!string.IsNullOrWhiteSpace(label)) existing.Label = label!.Trim();
                    if (removable) existing.Removable = true;
                }
            return existing;
        }

        var nested = FindNestingConflict(path);
        if (nested is not null)
            throw new InvalidOperationException(
                $"That folder is inside an existing backup folder ({nested.PathHint}). Pick a folder outside it.");

        Directory.CreateDirectory(path);
        var root = new BackupRoot
        {
            Label = string.IsNullOrWhiteSpace(label) ? SuggestLabel(path) : label!.Trim(),
            PathHint = path,
            LastSeen = DateTimeOffset.UtcNow,
            Removable = removable,
        };
        using (_store.Gate.Enter())   // 
        {
            _store.Gate.AssertHeld();
            M.Roots.Add(root);
        }
        return root;
    }

    // relocation
    public BackupRoot RepointRoot(string rootId, string newPath)
    {
        var root = M.Roots.FirstOrDefault(r => r.Id == rootId) ?? throw new ArgumentException($"No such root: {rootId}");
        newPath = Path.GetFullPath(newPath);
        if (!Directory.Exists(newPath)) throw new DirectoryNotFoundException($"Folder not found: {newPath}");
        var nested = FindNestingConflict(newPath);
        if (nested is not null && nested.Id != root.Id)
            throw new InvalidOperationException($"That folder is inside an existing backup folder ({nested.PathHint}). Pick a folder outside it.");
        using (_store.Gate.Enter())
        {
            root.PathHint = newPath;
            root.PortablePath = null;   // the next Bind recomputes it
            if (root.State == RootState.Offline) root.LastSeen = DateTimeOffset.UtcNow;
        }
        return root;
    }

    public BackupRoot? FindSamePath(string path)
    {
        var p = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return M.Roots.FirstOrDefault(r => !string.IsNullOrEmpty(r.PathHint)
            && string.Equals(Path.GetFullPath(r.PathHint).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), p, StringComparison.OrdinalIgnoreCase));
    }

    public BackupRoot? FindNestingConflict(string path)
    {
        path = Path.GetFullPath(path);
        foreach (var r in M.Roots)
        {
            if (string.IsNullOrEmpty(r.PathHint)) continue;
            var other = Path.GetFullPath(r.PathHint);
            if (Contains(other, path) || Contains(path, other)) return r;
        }
        return null;

        static bool Contains(string outer, string inner)
        {
            var o = outer.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var i = inner.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return i.StartsWith(o, StringComparison.OrdinalIgnoreCase) && !string.Equals(o, i, StringComparison.OrdinalIgnoreCase);
        }
    }

    public BackupRoot? FindSameVolume(string path)
    {
        var pr = Grog.Core.Storage.DriveResolver.MountRootOf(path);
        if (string.IsNullOrEmpty(pr)) return null;
        return M.Roots.FirstOrDefault(r => !string.IsNullOrEmpty(r.PathHint)
            && string.Equals(Grog.Core.Storage.DriveResolver.MountRootOf(r.PathHint), pr, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<BackupRoot> Roots => M.Roots;

    public BackupRoot? FindRoot(string labelOrId) =>
        M.Roots.FirstOrDefault(r =>
            string.Equals(r.Label, labelOrId, StringComparison.OrdinalIgnoreCase) ||
            r.Id.StartsWith(labelOrId, StringComparison.OrdinalIgnoreCase));

    public void SetPrimary(string labelOrId)
    {
        var root = FindRoot(labelOrId) ?? throw new ArgumentException($"No such root: {labelOrId}");
        _store.Mutate(m =>   // 
        {
            var old = m.PrimaryRootId;
            if (!string.IsNullOrEmpty(old) && old != root.Id)
            {
                foreach (var item in m.Items)
                    foreach (var f in item.Files.Concat(item.OldVersionFiles))
                        if (f.RootId is null && Sync.BackupScope.HoldsBytes(f)) f.RootId = old;
                foreach (var role in m.Routing.RoleRoots.Where(kv => kv.Value == old).Select(kv => kv.Key).ToList())
                    m.Routing.RoleRoots[role] = root.Id;
            }
            m.PrimaryRootId = root.Id;
        });
    }

    public void RenameRoot(string labelOrId, string? newLabel)
    {
        var root = FindRoot(labelOrId) ?? throw new ArgumentException($"No such root: {labelOrId}");
        var label = string.IsNullOrWhiteSpace(newLabel) ? SuggestLabel(root.PathHint) : newLabel!.Trim();
        using (_store.Gate.Enter()) root.Label = label;
    }

    public static bool HasCustomLabel(BackupRoot root)
        => !string.IsNullOrWhiteSpace(root.Label)
           && !string.Equals(root.Label, SuggestLabel(root.PathHint), StringComparison.OrdinalIgnoreCase)
           && !string.Equals(root.Label, "Primary", StringComparison.OrdinalIgnoreCase)
           && !string.Equals(root.Label, "Secondary", StringComparison.OrdinalIgnoreCase);

    public void SwapIntoSlot(string otherRootId, string slotRootId)
    {
        var other = FindRoot(otherRootId) ?? throw new ArgumentException($"No such root: {otherRootId}");
        var slot = FindRoot(slotRootId) ?? throw new ArgumentException($"No such root: {slotRootId}");
        if (other.Id == slot.Id) return;
        if (other.State != RootState.Detached) throw new InvalidOperationException($"{other.Label} already holds a slot.");
        ReattachRoot(other.Id);
        if (slot.Id == M.PrimaryRootId) SetPrimary(other.Id);
        DetachRoot(slot.Id);
    }

    public int RetargetRoles(string fromRootId, string toRootId)
    {
        int n = 0;
        using (_store.Gate.Enter())
        {
            foreach (var role in M.Routing.RoleRoots.Where(kv => kv.Value == fromRootId).Select(kv => kv.Key).ToList())
            { M.Routing.RoleRoots[role] = toRootId; n++; }
        }
        return n;
    }

    public void PinRole(string labelOrId, ContentRole? role)
    {
        var root = FindRoot(labelOrId) ?? throw new ArgumentException($"No such root: {labelOrId}");
        using (_store.Gate.Enter())   // 
        {
            if (role is null)
            {
                foreach (var r in M.Routing.RoleRoots.Where(kv => kv.Value == root.Id).Select(kv => kv.Key).ToList())
                    M.Routing.RoleRoots.Remove(r);
            }
            else M.Routing.SetRole(role.Value, root.Id);
        }
    }

    public void RemoveRoot(string labelOrId, bool force = false)
    {
        var root = FindRoot(labelOrId) ?? throw new ArgumentException($"No such root: {labelOrId}");
        if (root.Id == M.PrimaryRootId)
            throw new InvalidOperationException("Can't remove the primary root.");
        bool holdsFiles = FilesOnRoot(root.Id).Any();
        if (holdsFiles && !force)
            throw new InvalidOperationException("Root still holds files. Relocate them first, or use --force.");

        using (_store.Gate.Enter())   // 
        {
            // Detach role assignments 
            foreach (var role in M.Routing.RoleRoots.Where(kv => kv.Value == root.Id).Select(kv => kv.Key).ToList())
                M.Routing.RoleRoots[role] = M.PrimaryRootId!;

            foreach (var f in FilesOnRoot(root.Id).ToList())
            {
                if (f.IsOldVersion) { f.RootId = null; continue; }   // archive records: as before, not this reset's business
                if (string.IsNullOrEmpty(f.ResolvedFileName) && !string.IsNullOrEmpty(f.LocalRelativePath))
                    f.ResolvedFileName = System.IO.Path.GetFileName(f.LocalRelativePath.Replace('\\', '/'));
                f.ResetLocal();
            }

            M.Roots.Remove(root);
        }
    }

    public void DetachRoot(string labelOrId)
    {
        var root = FindRoot(labelOrId) ?? throw new ArgumentException($"No such root: {labelOrId}");
        if (root.Id == M.PrimaryRootId)
            throw new InvalidOperationException("Can't detach the primary root.");
        using (_store.Gate.Enter())
        {
            foreach (var role in M.Routing.RoleRoots.Where(kv => kv.Value == root.Id).Select(kv => kv.Key).ToList())
                M.Routing.RoleRoots[role] = M.PrimaryRootId!;
            root.State = RootState.Detached;
        }
    }

    public void ReattachRoot(string labelOrId)
    {
        var root = FindRoot(labelOrId) ?? throw new ArgumentException($"No such root: {labelOrId}");
        using (_store.Gate.Enter())
            if (root.State == RootState.Detached) root.State = RootState.Offline;
    }

    public IEnumerable<BackupRoot> ActiveRoots => M.Roots.Where(r => r.State != RootState.Detached);
    public IEnumerable<BackupRoot> DetachedRoots => M.Roots.Where(r => r.State == RootState.Detached);

    // policy

    public void SetRolePolicy(ContentRole role, string labelOrId)
    {
        var root = FindRoot(labelOrId) ?? throw new ArgumentException($"No such root: {labelOrId}");
        _store.Mutate(m => m.Routing.SetRole(role, root.Id));   // 
    }

    // relocate

    public Task<RelocateResult> RelocateGameAsync(LibraryItem game, string targetLabelOrId,
        IBackupLayout layout, CancellationToken ct = default)
        => RelocateFilesAsync(game.Files.Select(f => (game, f)), targetLabelOrId, layout, ct);

    public async Task<RelocateResult> RelocateFilesAsync(IEnumerable<(LibraryItem Game, GameFile File)> files,
        string targetLabelOrId, IBackupLayout layout, CancellationToken ct = default)
    {
        var target = FindRoot(targetLabelOrId) ?? throw new ArgumentException($"No such root: {targetLabelOrId}");
        var targetRootPath = layout.RootPath(target.Id)
            ?? throw new InvalidOperationException($"Target root '{target.Label}' is offline.");

        var result = new RelocateResult();
        foreach (var (game, f) in files)
        {
            ct.ThrowIfCancellationRequested();
            if (f.RootId == target.Id) continue; // already there

            var src = layout.ResolvePath(f);
            var slug = string.IsNullOrEmpty(game.Slug) ? Format.Naming.Slug(game.Title) : game.Slug;

            var destDir = Path.Combine(targetRootPath, layout.SubPathFor(slug, f.Kind, f, game.GogId, target.Id));

            if (src is not null && File.Exists(src))
            {
                Directory.CreateDirectory(destDir);
                var dest = Path.Combine(destDir, Path.GetFileName(src));
                if (File.Exists(dest)) File.Delete(dest);
                File.Move(src, dest);
                using (_store.Gate.Enter())   // 
                {
                    f.RootId = target.Id;
                    f.LocalRelativePath = Path.GetRelativePath(targetRootPath, dest).Replace('\\', '/');
                }
                result.Moved++;
                await _store.SaveAsync(ct);
            }
            else
            {
                using (_store.Gate.Enter())   // 
                {
                    f.RootId = target.Id;
                    if (Sync.BackupScope.IsPresent(f.State)) f.State = FileState.Missing;
                }
                result.Repointed++;
            }
        }

        await _store.SaveAsync(ct);
        return result;
    }

    // replace (drive recovery)

    public void ReplaceRoot(string labelOrId, string newPath)
    {
        var root = FindRoot(labelOrId) ?? throw new ArgumentException($"No such root: {labelOrId}");
        newPath = Path.GetFullPath(newPath);
        Directory.CreateDirectory(newPath);

        var newId = Guid.NewGuid().ToString("N")[..12];
        using (_store.Gate.Enter())   // 
        {
            root.PriorIds.Add(root.Id);

            foreach (var f in FilesOnRoot(root.Id).ToList())
            {
                f.RootId = newId;
                if (Sync.BackupScope.IsPresent(f.State)) f.State = FileState.Missing;
            }
            foreach (var role in M.Routing.RoleRoots.Where(kv => kv.Value == root.Id).Select(kv => kv.Key).ToList())
                M.Routing.RoleRoots[role] = newId;
            if (M.PrimaryRootId == root.Id) M.PrimaryRootId = newId;

            root.Id = newId;
            root.PathHint = newPath;
            root.PortablePath = null;
            root.State = RootState.Online;
            root.LastSeen = DateTimeOffset.UtcNow;
        }
    }


    // disk preflight

    public long? FreeBytesForRoot(string rootId, IBackupLayout layout)
    {
        var path = layout.RootPath(rootId);
        if (path is null) return null;
        return Grog.Core.Storage.DriveResolver.FreeBytes(path);
    }

    public long? FreeBytesAfterPendingForRoot(string rootId, IBackupLayout layout)
    {
        var free = FreeBytesForRoot(rootId, layout);
        if (free is null) return null;
        return Math.Max(0L, free.Value - (_pending?.ForVolume(layout.RootPath(rootId)) ?? 0L));
    }

    public List<DeviceSpace> DeviceSpaces(IBackupLayout layout)
    {
        UiThreadGuard.NotOnUi("a drive free-space probe");
        var m = _store.Current;
        return m.Roots
            .Where(r => r.State != RootState.Detached)   // a shelved drive is not a placement target
            .OrderByDescending(r => r.Id == m.PrimaryRootId)
            .Select(r => DeviceSpace.ForRoot(r.Id, Math.Max(0L, (FreeBytesForRoot(r.Id, layout) ?? 0L) - (_pending?.ForVolume(layout.RootPath(r.Id)) ?? 0L)), layout.IsOnline(r.Id), layout.RootPath(r.Id)))
            .ToList();
    }

    // helpers

    private IEnumerable<GameFile> FilesOnRoot(string rootId) =>
        M.Items.SelectMany(i => i.Files.Concat(i.OldVersionFiles))
               .Where(f => (f.RootId ?? M.PrimaryRootId) == rootId);

    private static string SuggestLabel(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "Root";
        var name = new DirectoryInfo(path).Name;
        return string.IsNullOrWhiteSpace(name) ? "Root" : name;
    }

}

public sealed class RelocateResult
{
    public int Moved { get; set; }
    public int Repointed { get; set; }
}
