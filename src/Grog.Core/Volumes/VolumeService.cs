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

/// <summary>
/// User-facing multi-volume operations, built on the manifest's roots + routing policy. Keeps all
/// root lifecycle logic in one place: adding/removing drives, assigning roles, moving a game's files
/// between drives, and recovering a dead drive onto a replacement.
/// </summary>
public sealed class VolumeService
{
    private readonly IManifestStore _store;
    public VolumeService(IManifestStore store, PendingWrites? pending = null) { _store = store; _pending = pending; }
    private readonly PendingWrites? _pending;

    private LibraryManifest M => _store.Current;

    // ---- roots -----------------------------------------------------------------------------

    /// <summary>Registers a new backup root at a path. Returns the created (or existing) root.</summary>
    public BackupRoot AddRoot(string path, string? label = null, bool removable = false)
    {
        path = Path.GetFullPath(path);
        // Idempotent: adding a folder that's already a root returns the existing one (no duplicates).
        var existing = M.Roots.FirstOrDefault(r =>
            !string.IsNullOrEmpty(r.PathHint) &&
            string.Equals(Path.GetFullPath(r.PathHint), path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            // (09-14) Re-adding a DETACHED root's folder is the user asking for it back: reattach so the layout
            // scan binds it and it takes a slot again. Returning it still Detached left "Scan and Add" adopting
            // 0 files into a root that never showed up (measured, bg2025 grog.log 14:05).
            using (_store.Gate.Enter())
                if (existing.State == RootState.Detached)
                {
                    existing.State = RootState.Offline;   // LastSeen is the volume scan's to stamp (Bind)
                    if (!string.IsNullOrWhiteSpace(label)) existing.Label = label!.Trim();
                    if (removable) existing.Removable = true;
                }
            return existing;
        }

        // A root inside another root is rejected: its bytes would be double-counted and a reorg of the
        // outer root would walk into the inner one.
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
        using (_store.Gate.Enter())   // (manifest gate 09-08)
        {
            _store.Gate.AssertHeld();
            M.Roots.Add(root);
        }
        return root;
    }

    // ---- relocation (marker search) ------------------------------------------------------------
    // A root whose last-known path is gone (drive re-lettered, mount point renamed) is looked for by its marker on
    // every mounted volume. The find is a FACT handed to the host; RepointRoot is the user's answer.

    /// <summary>The storage is at a new path (Scan and Add matched its files there, or Change Folder finished): the
    /// hint moves, files are untouched (their paths are relative to the root), and the next volume scan binds it.</summary>
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

    /// <summary>The existing root that contains <paramref name="path"/>, or that <paramref name="path"/>
    /// would contain. Null when the folder is independent of every root.</summary>
    /// <summary>The root already tracked at exactly <paramref name="path"/> (any state), or null.</summary>
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

    /// <summary>True when <paramref name="path"/> sits on the same physical volume as an existing root.
    /// Allowed (a second folder is still useful), but worth warning about: one failure loses both copies.</summary>
    public BackupRoot? FindSameVolume(string path)
    {
        // Compare real mount points, not Path.GetPathRoot ("/" for every path on Linux).
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

    /// <summary>Makes an existing root the primary (default) device -- where new backups go first.</summary>
    public void SetPrimary(string labelOrId)
    {
        var root = FindRoot(labelOrId) ?? throw new ArgumentException($"No such root: {labelOrId}");
        _store.Mutate(m =>   // (manifest gate 09-08)
        {
            // (09-14) A file with no RootId RESOLVES to the primary. Changing the primary without stamping them
            // would silently re-attribute every such file to the new drive while its bytes stay on the old one.
            // Stamp the old primary's id first; then the switch moves nothing but the role.
            var old = m.PrimaryRootId;
            if (!string.IsNullOrEmpty(old) && old != root.Id)
            {
                // Only records that HOLD bytes (review 09-15): a Forgotten drive's leftovers keep a path with no
                // bytes behind it, and stamping them with the outgoing primary strands them on a shelved drive.
                foreach (var item in m.Items)
                    foreach (var f in item.Files.Concat(item.OldVersionFiles))
                        if (f.RootId is null && Sync.BackupScope.HoldsBytes(f)) f.RootId = old;
                // The ROLE pins follow the role: BackupLayout.EnsurePrimary stamps every RoleRoot with the first
                // primary explicitly, so without this "Make Primary" left new downloads going to the old drive
                // (review 09-15).
                foreach (var role in m.Routing.RoleRoots.Where(kv => kv.Value == old).Select(kv => kv.Key).ToList())
                    m.Routing.RoleRoots[role] = root.Id;
            }
            m.PrimaryRootId = root.Id;
        });
    }

    /// <summary>(09-15) The user's name for a drive. Stored on the root, so it survives Change Folder and a move
    /// of the folder. Blank resets to the folder name.</summary>
    public void RenameRoot(string labelOrId, string? newLabel)
    {
        var root = FindRoot(labelOrId) ?? throw new ArgumentException($"No such root: {labelOrId}");
        var label = string.IsNullOrWhiteSpace(newLabel) ? SuggestLabel(root.PathHint) : newLabel!.Trim();
        using (_store.Gate.Enter()) root.Label = label;
    }

    /// <summary>True when the label is the user's own: not the folder name (the default) and not the role words
    /// older builds stamped ("Primary"/"Secondary"), which the page heading already says.</summary>
    public static bool HasCustomLabel(BackupRoot root)
        => !string.IsNullOrWhiteSpace(root.Label)
           && !string.Equals(root.Label, SuggestLabel(root.PathHint), StringComparison.OrdinalIgnoreCase)
           && !string.Equals(root.Label, "Primary", StringComparison.OrdinalIgnoreCase)
           && !string.Equals(root.Label, "Secondary", StringComparison.OrdinalIgnoreCase);

    /// <summary>(09-14) Other storage takes a slot from the drive holding it, in one step: the slot holder goes to
    /// the shelf (Detached), the other drive comes off it. For the primary slot the role moves first (SetPrimary
    /// stamps the outgoing primary's files), then the old primary is detached. Files never move; the queue is
    /// re-planned by the caller.</summary>
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

    /// <summary>Every role pin that pointed at <paramref name="fromRootId"/> now points at
    /// <paramref name="toRootId"/> (09-13). SetPrimary alone leaves the pins, so after "move everything and make
    /// it primary" a pinned role would still route new downloads onto the drive that just emptied. This is the
    /// storage half of a placement following its drive, not a shape change, so it does not go through
    /// ExtrasPlacementRun.</summary>
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

    /// <summary>Pins a content role to a device, or clears its pins (null role = plain overflow). A pinned
    /// role dedicates the device; dedicated devices never cross-spill -- filling one is a "won't fit" warning.</summary>
    public void PinRole(string labelOrId, ContentRole? role)
    {
        var root = FindRoot(labelOrId) ?? throw new ArgumentException($"No such root: {labelOrId}");
        using (_store.Gate.Enter())   // (manifest gate 09-08)
        {
            if (role is null)
            {
                foreach (var r in M.Routing.RoleRoots.Where(kv => kv.Value == root.Id).Select(kv => kv.Key).ToList())
                    M.Routing.RoleRoots.Remove(r);
            }
            else M.Routing.SetRole(role.Value, root.Id);
        }
    }

    /// <summary>Removes a root. Refuses if it is the primary or still holds files (unless forced).</summary>
    public void RemoveRoot(string labelOrId, bool force = false)
    {
        var root = FindRoot(labelOrId) ?? throw new ArgumentException($"No such root: {labelOrId}");
        if (root.Id == M.PrimaryRootId)
            throw new InvalidOperationException("Can't remove the primary root.");
        bool holdsFiles = FilesOnRoot(root.Id).Any();
        if (holdsFiles && !force)
            throw new InvalidOperationException("Root still holds files. Relocate them first, or use --force.");

        using (_store.Gate.Enter())   // (manifest gate 09-08)
        {
            // Detach role assignments / overrides pointing at it (fall back to primary).
            foreach (var role in M.Routing.RoleRoots.Where(kv => kv.Value == root.Id).Select(kv => kv.Key).ToList())
                M.Routing.RoleRoots[role] = M.PrimaryRootId!;

            // The device is gone: stop counting its files as backed up. Bytes stay on the removed drive (never
            // deleted); re-adding the drive later re-matches them via import.
            foreach (var f in FilesOnRoot(root.Id).ToList())
            {
                if (f.IsOldVersion) { f.RootId = null; continue; }   // archive records: as before, not this reset's business
                // The import that re-adopts these on a re-add matches by the record's own NAME first (09-17), and
                // for a file that never had a CDN name recorded the leaf of its last path is that name: keep it
                // before the path goes.
                if (string.IsNullOrEmpty(f.ResolvedFileName) && !string.IsNullOrEmpty(f.LocalRelativePath))
                    f.ResolvedFileName = System.IO.Path.GetFileName(f.LocalRelativePath.Replace('\\', '/'));
                // THE reset (GameFile.ResetLocal). The copy here cleared RootId and flipped three states, which
                // left LocalRelativePath behind: with RootId null that path RESOLVED TO THE PRIMARY, and a
                // Corrupt or Missing file kept its state pointing at a drive that is gone.
                f.ResetLocal();
            }

            M.Roots.Remove(root);
        }
    }

    /// <summary>Put a drive on the shelf (owner, 09-13): its files stay recorded and count as backed up, nothing
    /// is re-queued, and it frees its storage slot. Not the primary. Routing that pointed at it falls back to
    /// the primary, as on removal: nothing can be placed on a shelf.</summary>
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

    /// <summary>Take a detached drive back into a slot: Offline until the next volume scan finds its marker.</summary>
    public void ReattachRoot(string labelOrId)
    {
        var root = FindRoot(labelOrId) ?? throw new ArgumentException($"No such root: {labelOrId}");
        using (_store.Gate.Enter())
            if (root.State == RootState.Detached) root.State = RootState.Offline;
    }

    /// <summary>Roots in the two storage slots: everything not on the shelf.</summary>
    public IEnumerable<BackupRoot> ActiveRoots => M.Roots.Where(r => r.State != RootState.Detached);
    public IEnumerable<BackupRoot> DetachedRoots => M.Roots.Where(r => r.State == RootState.Detached);

    // ---- policy ----------------------------------------------------------------------------

    public void SetRolePolicy(ContentRole role, string labelOrId)
    {
        var root = FindRoot(labelOrId) ?? throw new ArgumentException($"No such root: {labelOrId}");
        _store.Mutate(m => m.Routing.SetRole(role, root.Id));   // (manifest gate 09-08)
    }

    // ---- relocate --------------------------------------------------------------------------

    /// <summary>Moves all of a game's files to a target root. Present files on online roots move physically;
    /// missing/offline files are re-pointed and left for a later download onto the new root.</summary>
    public Task<RelocateResult> RelocateGameAsync(LibraryItem game, string targetLabelOrId,
        IBackupLayout layout, CancellationToken ct = default)
        => RelocateFilesAsync(game.Files.Select(f => (game, f)), targetLabelOrId, layout, ct);

    /// <summary>Moves specific files to another device -- per-file because a game can straddle drives.
    /// Destination comes from the layout, so a move respects the chosen folder shape.</summary>
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

            // Folder shape from the layout, root from the user's pick. ResolveTargetDir can't be used here:
            // it derives the root from routing policy, which would send the file straight back.
            var destDir = Path.Combine(targetRootPath, layout.SubPathFor(slug, f.Kind, f, game.GogId, target.Id));

            if (src is not null && File.Exists(src))
            {
                Directory.CreateDirectory(destDir);
                var dest = Path.Combine(destDir, Path.GetFileName(src));
                if (File.Exists(dest)) File.Delete(dest);
                File.Move(src, dest);
                // Re-point the manifest only after the move succeeded, so a mid-move failure can't leave the
                // entry pointing at a root it never reached. Normalize to '/' per the manifest convention;
                // persist per file so an interrupted batch never orphans files already moved.
                using (_store.Gate.Enter())   // (manifest gate 09-08)
                {
                    f.RootId = target.Id;
                    f.LocalRelativePath = Path.GetRelativePath(targetRootPath, dest).Replace('\\', '/');
                }
                result.Moved++;
                await _store.SaveAsync(ct);
            }
            else
            {
                // Not on disk (or offline): re-point only; a download will place it on the new root.
                using (_store.Gate.Enter())   // (manifest gate 09-08)
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

    // ---- replace (drive recovery) ----------------------------------------------------------

    /// <summary>Replaces a dead/lost root with a new physical location: the logical root keeps its label,
    /// gets a new id (old id filed into PriorIds), and every file is re-pointed and marked missing. An import
    /// pass beforehand can adopt survivors; a download run re-fetches the rest.</summary>
    public void ReplaceRoot(string labelOrId, string newPath)
    {
        var root = FindRoot(labelOrId) ?? throw new ArgumentException($"No such root: {labelOrId}");
        newPath = Path.GetFullPath(newPath);
        Directory.CreateDirectory(newPath);

        var newId = Guid.NewGuid().ToString("N")[..12];
        using (_store.Gate.Enter())   // (manifest gate 09-08)
        {
            root.PriorIds.Add(root.Id);

            // Re-point files, policy roles, and overrides from old id to new id.
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


    // ---- disk preflight --------------------------------------------------------------------

    /// <summary>Free bytes available on the drive backing a root, or null if unknown/offline.</summary>
    public long? FreeBytesForRoot(string rootId, IBackupLayout layout)
    {
        var path = layout.RootPath(rootId);
        if (path is null) return null;
        return Grog.Core.Storage.DriveResolver.FreeBytes(path);
    }

    /// <summary>Free bytes on a root's drive less what a running move still has to write there, or null if
    /// unknown/offline. The room a new move onto that root can count on.</summary>
    public long? FreeBytesAfterPendingForRoot(string rootId, IBackupLayout layout)
    {
        var free = FreeBytesForRoot(rootId, layout);
        if (free is null) return null;
        return Math.Max(0L, free.Value - (_pending?.ForVolume(layout.RootPath(rootId)) ?? 0L));
    }

    /// <summary>Every registered root as a placement input, primary first (the planner fills in list
    /// order). Offline roots report zero free. Three callers built this by hand before 09-02.</summary>
    public List<DeviceSpace> DeviceSpaces(IBackupLayout layout)
    {
        UiThreadGuard.NotOnUi("a drive free-space probe");
        var m = _store.Current;
        return m.Roots
            .Where(r => r.State != RootState.Detached)   // a shelved drive is not a placement target
            .OrderByDescending(r => r.Id == m.PrimaryRootId)
            // (09-22) Less what a running move still has to write onto that volume: moves and downloads run together.
            .Select(r => DeviceSpace.ForRoot(r.Id, Math.Max(0L, (FreeBytesForRoot(r.Id, layout) ?? 0L) - (_pending?.ForVolume(layout.RootPath(r.Id)) ?? 0L)), layout.IsOnline(r.Id), layout.RootPath(r.Id)))
            .ToList();
    }

    // ---- helpers ---------------------------------------------------------------------------

    /// <summary>Every tracked file that effectively lives on <paramref name="rootId"/>: current builds, the
    /// Old Versions/ archive, and null-RootId files (which resolve to the primary via the same
    /// <c>?? PrimaryRootId</c> fallback the rest of the app uses). Remove/replace must cover all three.</summary>
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
