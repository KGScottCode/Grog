// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.IO;
using System.Linq;
using Grog.Core.Manifest;
using Grog.Core.Models;

namespace Grog.Core.Volumes;

/// <summary>Builds a <see cref="ReorgJob"/> from a desired end-state. The runner executes it; this
/// only decides what moves where. The "where a file belongs" logic is shared with
/// <see cref="LayoutMigrationService.NewRelativeFor"/> so there's exactly one definition.</summary>
public static class ReorgPlanner
{
    /// <summary>Human label for a root ("Archive", "USB Backup"), for blocked-move messages. Falls back to
    /// a generic phrase when the root has no label or can't be found.</summary>
    private static string DriveLabel(LibraryManifest m, string? rootId)
    {
        var r = m.Roots.FirstOrDefault(x => x.Id == rootId);
        return string.IsNullOrWhiteSpace(r?.Label) ? "a backup drive" : r!.Label;
    }

    /// <summary>Plans the moves to bring existing backups into the current <c>ExtrasLayout</c> shape.
    /// Every move stays on its root (atomic rename); correct files are skipped, offline roots left alone.</summary>
    public static ReorgJob ForLayoutChange(LibraryManifest m, IBackupLayout layout)
    {
        var job = new ReorgJob { Reason = ReorgReason.Layout };

        foreach (var item in m.Items)
        {
            var slug = item.Slug;
            foreach (var f in item.Files)
            {
                var rel = f.LocalRelativePath;
                if (string.IsNullOrEmpty(rel)) continue;

                var toRel = LayoutMigrationService.NewRelativeFor(rel, slug, f.Kind, m.ExtrasLayout, f);
                if (toRel is null) continue;   // already correct

                var rootId = m.EffectiveRootId(f);
                if (!layout.IsOnline(rootId))
                {
                    // Drive offline/missing: record it so the caller blocks the whole job rather than
                    // silently dropping these moves.
                    job.NoteUnavailableDrive(DriveLabel(m, rootId));
                    continue;
                }

                var fromAbs = layout.ResolvePath(f);
                var root = layout.RootPath(rootId);
                if (fromAbs is null || root is null) continue;   // never-downloaded leaf: runner skips + repaths

                var toAbs = System.IO.Path.Combine(root, toRel.Replace('/', System.IO.Path.DirectorySeparatorChar));

                job.Moves.Add(new ReorgMove
                {
                    GogId = item.GogId,
                    FileKey = f.FileKey,
                    Title = item.Title,
                    FromRel = rel,
                    ToRel = toRel,
                    ToRootId = null,               // same root -> rename
                    FromRootId = f.RootId,
                    FromAbs = fromAbs,
                    ToAbs = toAbs,
                    SizeBytes = Grog.Core.Sync.Rollups.HeldBytesOf(f),
                    SizeIsLabel = f.LocalSizeBytes is null,
                    ExpectedMd5 = f.ExpectedMd5,
                    Kind = ReorgMoveKind.Rename,
                });
            }
        }

        return job;
    }

    /// <summary>Plans the moves for repointing a backup folder: every tracked file on the root keeps its
    /// relative path (src = oldPath/rel, dst = newPath/rel), the root is unchanged, and after the job settles
    /// the caller flips the root's PathHint. Pure -- no disk touched.</summary>
    public static ReorgJob ForRootPathChange(LibraryManifest m, string rootId, string oldPath, string newPath)
    {
        var job = new ReorgJob { Reason = ReorgReason.ChangeFolder, RepointRootId = rootId, RepointPath = Path.GetFullPath(newPath) };

        var sameVolume = string.Equals(
            Path.GetPathRoot(Path.GetFullPath(oldPath)),
            Path.GetPathRoot(Path.GetFullPath(newPath)),
            StringComparison.OrdinalIgnoreCase);
        var kind = sameVolume ? ReorgMoveKind.Rename : ReorgMoveKind.CopyVerifyDelete;

        foreach (var item in m.Items)
        {
            // Include OldVersionFiles: a whole-folder repoint must carry the irreplaceable Old Versions/
            // archive too, or it is silently stranded on the old drive. Same src=old/rel -> dst=new/rel logic.
            foreach (var f in item.Files.Concat(item.OldVersionFiles))
            {
                var fileRoot = m.EffectiveRootId(f);
                if (fileRoot != rootId) continue;
                var rel = f.LocalRelativePath;
                if (string.IsNullOrEmpty(rel)) continue;

                var native = rel.Replace('\\', '/').Replace('/', Path.DirectorySeparatorChar);

                job.Moves.Add(new ReorgMove
                {
                    GogId = item.GogId,
                    FileKey = f.FileKey,
                    Title = item.Title,
                    FromRel = rel,
                    ToRel = rel,                   // relative path unchanged: only the root folder moves
                    ToRootId = null,               // same root -> Repath is a no-op; caller flips PathHint
                    FromRootId = f.RootId,
                    FromAbs = Path.Combine(oldPath, native),
                    ToAbs = Path.Combine(newPath, native),
                    SizeBytes = Grog.Core.Sync.Rollups.HeldBytesOf(f),
                    SizeIsLabel = f.LocalSizeBytes is null,
                    ExpectedMd5 = f.ExpectedMd5,
                    Kind = kind,
                });
            }
        }

        return job;
    }

    /// <summary>Plans relocating a specific set of files onto <paramref name="targetRootId"/> (the one move
    /// path behind device-grid buttons and drag-drop). Folder shape comes from the layout; the root is the
    /// device the user picked. Files already on the target are skipped. Pure -- the runner touches disk.
    /// With <paramref name="room"/>, a cross-volume move that cannot land (over the per-file limit, or past the
    /// free bytes left after the moves already planned here) goes to <see cref="ReorgJob.NotFitting"/> instead;
    /// null checks nothing.</summary>
    /// <summary>A resumed job is re-checked the way a new one is planned: the pending cross-volume moves that no longer
    /// fit the target (room, in order, or its per-file limit) are marked Failed with the reason and listed in
    /// <see cref="ReorgJob.NotFitting"/>, so a resume never retries a file the drive cannot take. Returns how many.</summary>
    /// <summary>(QA 09-30 B3) Re-derive every unsettled move's absolute paths from where its roots are NOW: the journal
    /// stores absolute paths, and a drive that came back under another letter (or a relocation the user accepted)
    /// left them pointing at a volume that is gone or is someone else's. A root the layout cannot place keeps the
    /// journal's path. A Change Folder job's destination is its own new folder, not a root. Returns how many changed.</summary>
    public static int Rebase(ReorgJob job, LibraryManifest m, IBackupLayout layout)
    {
        int changed = 0;
        lock (job.SyncRoot)
        {
            foreach (var mv in job.Moves)
            {
                if (mv.IsSettled) continue;
                var file = m.ItemById(mv.GogId)?.Files.Concat(m.ItemById(mv.GogId)!.OldVersionFiles).FirstOrDefault(f => f.FileKey == mv.FileKey);
                var fromRoot = mv.FromRootId ?? (file is not null ? m.EffectiveRootId(file) : null) ?? m.PrimaryRootId;
                if (layout.RootPath(fromRoot) is { } fromBase && mv.FromRel.Length > 0)
                {
                    var abs = Path.Combine(fromBase, Native(mv.FromRel));
                    if (!string.Equals(abs, mv.FromAbs, StringComparison.OrdinalIgnoreCase)) { mv.FromAbs = abs; changed++; }
                }
                var toBase = job.Reason == ReorgReason.ChangeFolder ? job.RepointPath : layout.RootPath(mv.ToRootId ?? fromRoot);
                if (toBase is not null && mv.ToRel.Length > 0)
                {
                    var abs = Path.Combine(toBase, Native(mv.ToRel));
                    if (!string.Equals(abs, mv.ToAbs, StringComparison.OrdinalIgnoreCase)) { mv.ToAbs = abs; changed++; }
                }
            }
        }
        return changed;
        static string Native(string rel) => rel.Replace('\\', '/').Replace('/', Path.DirectorySeparatorChar);
    }

    public static int DropWhatNoLongerFits(ReorgJob job, MoveRoom room)
    {
        long roomLeft = room.FreeBytes;
        int dropped = 0;
        lock (job.SyncRoot)
        {
            // Dropped moves LEAVE the plan: marked Failed they were re-admitted by the next run (it retries every
            // Failed move) and copied into a full drive until the breaker paused the job (QA 09-30 B4). The file
            // stays where it is, untouched, and NotFitting says so.
            foreach (var mv in job.Moves.ToList())
            {
                if (mv.State is not (ReorgMoveState.Pending or ReorgMoveState.Failed) || mv.Kind != ReorgMoveKind.CopyVerifyDelete) continue;
                var name = Path.GetFileName(mv.ToAbs);
                string? reason = mv.SizeBytes > room.MaxFileBytes ? room.LimitReason
                    : room.Charge(mv.SizeBytes) > roomLeft ? MoveRoom.NoRoomReason : null;
                if (reason is null) { roomLeft -= room.Charge(mv.SizeBytes); continue; }
                job.Moves.Remove(mv);
                job.NotFitting.Add(new ReorgSkipped(mv.Title, name, mv.SizeBytes, reason));
                dropped++;
            }
        }
        return dropped;
    }

    public static ReorgJob ForFileMoves(LibraryManifest m, IBackupLayout layout,
        System.Collections.Generic.IEnumerable<(LibraryItem Item, GameFile File)> files, string targetRootId,
        MoveRoom? room = null)
    {
        var job = new ReorgJob { Reason = ReorgReason.Relocate };
        long roomLeft = room?.FreeBytes ?? long.MaxValue;
        var targetRootPath = layout.RootPath(targetRootId);
        if (targetRootPath is null)
        {
            // Target drive offline/missing: record it so the caller blocks with a clear message.
            job.NoteUnavailableDrive(DriveLabel(m, targetRootId));
            return job;
        }

        var targetVolume = Grog.Core.Storage.DriveResolver.MountRootOf(targetRootPath) ?? Path.GetPathRoot(Path.GetFullPath(targetRootPath));

        foreach (var (item, f) in files)
        {
            var curRoot = m.EffectiveRootId(f);
            if (curRoot == targetRootId) continue;   // already on the target device

            if (!layout.IsOnline(curRoot))
            {
                // Source drive offline/missing: record it so the caller blocks the whole move instead of
                // quietly skipping the stranded files.
                job.NoteUnavailableDrive(DriveLabel(m, curRoot));
                continue;
            }

            var src = layout.ResolvePath(f);
            string toRel; string toAbs;
            if (f.IsOldVersion && !string.IsNullOrEmpty(f.LocalRelativePath))
            {
                // A retained build lives under Old Versions/, not the Games/Extras layout: keep its relative
                // path verbatim so the archive shape is preserved on the target device.
                toRel = f.LocalRelativePath!.Replace('\\', '/');
                toAbs = Path.Combine(targetRootPath, toRel.Replace('/', Path.DirectorySeparatorChar));
            }
            else
            {
                var slug = string.IsNullOrEmpty(item.Slug) ? Grog.Core.Format.Naming.Slug(item.Title) : item.Slug;
                var subPath = layout.SubPathFor(slug, f.Kind, f, item.GogId, targetRootId);
                var fileName = src is not null ? Path.GetFileName(src)
                    : (!string.IsNullOrEmpty(f.LocalRelativePath) ? Path.GetFileName(f.LocalRelativePath) : f.FileKey);
                var toRelNative = Path.Combine(subPath, fileName);
                toRel = toRelNative.Replace('\\', '/');
                toAbs = Path.Combine(targetRootPath, toRelNative.Replace('/', Path.DirectorySeparatorChar));
            }

            // Same physical volume is an instant rename; otherwise copy -> verify -> delete.
            var srcVolume = src is not null ? (Grog.Core.Storage.DriveResolver.MountRootOf(src) ?? Path.GetPathRoot(Path.GetFullPath(src))) : null;
            var sameVolume = srcVolume is not null &&
                string.Equals(srcVolume, targetVolume, StringComparison.OrdinalIgnoreCase);
            var size = Grog.Core.Sync.Rollups.HeldBytesOf(f);

            // A file that cannot fit is never queued: the copy would fail part-way and leave the user to sort it out.
            if (room is not null && !sameVolume)
            {
                var name = Path.GetFileName(toAbs);
                if (size > room.MaxFileBytes) { job.NotFitting.Add(new ReorgSkipped(item.Title, name, size, room.LimitReason)); continue; }
                // The same arithmetic the download planner uses: the size rounded to the target's allocation unit.
                long charged = room.Charge(size);
                if (charged > roomLeft) { job.NotFitting.Add(new ReorgSkipped(item.Title, name, size, MoveRoom.NoRoomReason)); continue; }
                roomLeft -= charged;
            }

            job.Moves.Add(new ReorgMove
            {
                GogId = item.GogId,
                FileKey = f.FileKey,
                Title = item.Title,
                FromRel = f.LocalRelativePath ?? "",
                ToRel = toRel,
                ToRootId = targetRootId,        // cross-root move: Repath flips RootId to the target
                FromRootId = f.RootId,          // remembered so a revert can put it back
                FromAbs = src ?? "",
                ToAbs = toAbs,
                SizeBytes = size,
                SizeIsLabel = f.LocalSizeBytes is null,
                ExpectedMd5 = f.ExpectedMd5,
                Kind = sameVolume ? ReorgMoveKind.Rename : ReorgMoveKind.CopyVerifyDelete,
            });
        }
        return job;
    }
}
