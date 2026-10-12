// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.IO;
using System.Linq;
using Grog.Core.Manifest;
using Grog.Core.Models;

namespace Grog.Core.Volumes;

public static class ReorgPlanner
{
    private static string DriveLabel(LibraryManifest m, string? rootId)
    {
        var r = m.Roots.FirstOrDefault(x => x.Id == rootId);
        return string.IsNullOrWhiteSpace(r?.Label) ? "a backup drive" : r!.Label;
    }

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
                job.NoteUnavailableDrive(DriveLabel(m, curRoot));
                continue;
            }

            var src = layout.ResolvePath(f);
            string toRel; string toAbs;
            if (f.IsOldVersion && !string.IsNullOrEmpty(f.LocalRelativePath))
            {
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

            // Same physical volume is an instant rename
            var srcVolume = src is not null ? (Grog.Core.Storage.DriveResolver.MountRootOf(src) ?? Path.GetPathRoot(Path.GetFullPath(src))) : null;
            var sameVolume = srcVolume is not null &&
                string.Equals(srcVolume, targetVolume, StringComparison.OrdinalIgnoreCase);
            var size = Grog.Core.Sync.Rollups.HeldBytesOf(f);

            // A file that cannot fit is never queued
            if (room is not null && !sameVolume)
            {
                var name = Path.GetFileName(toAbs);
                if (size > room.MaxFileBytes) { job.NotFitting.Add(new ReorgSkipped(item.Title, name, size, room.LimitReason)); continue; }
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
