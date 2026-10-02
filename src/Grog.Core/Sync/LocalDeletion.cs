// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Volumes;

namespace Grog.Core.Sync;

/// <summary>
/// The ONE place Grog deletes a user's downloaded bytes, and it does so only on the user's explicit
/// Delete (right-click on a file, a game, or a section -- owner, 09-11; this reverses the older "never
/// delete user files" rule, which now reads "never delete without being told to"). Per file: the copy on
/// disk goes, so do its archived builds under Old Versions/ (they are builds OF that file; a file the user
/// no longer wants has no revert path to keep) and its resumable .part, the record goes back to
/// never-downloaded, and the now-empty folders Grog made are removed. A file that is queued or in flight is
/// skipped, not raced: the download is the newer intent.
///
/// THREE STEPS, so the disk work never runs under the manifest gate (09-13): <see cref="Plan"/> reads the
/// records and resolves paths (gate held), <see cref="Execute"/> touches the disk (no gate), <see cref="Apply"/>
/// rewrites the records for what actually went (gate held). <see cref="Delete"/> composes them for callers
/// that already hold the gate and do not mind (tests, the CLI).
/// </summary>
public static class LocalDeletion
{
    public sealed record Result(int Deleted, long Bytes, int Skipped, int Missing, List<string> Failures)
    {
        public static readonly Result Empty = new(0, 0, 0, 0, new());
    }

    /// <summary>One file the plan will remove: the record, its absolute path, and the archived builds and
    /// .part that go with it. <c>RootPath</c> is the drive it is on, so Execute can tell "file gone" from
    /// "drive gone".</summary>
    public sealed record Target(GameFile File, string Path, string RootPath, IReadOnlyList<(GameFile Old, string Path)> OldVersions, string? PartPath);

    public sealed record DeletePlan(IReadOnlyList<Target> Targets, int Skipped, List<string> Failures);

    /// <summary>What Execute did to one target, for Apply.</summary>
    public sealed record Outcome(Target Target, bool Cleared, bool WasMissing, long Bytes, List<GameFile> OldVersionsGone);

    /// <summary>What a Delete on these files would do, for the confirmation text: files with a copy on
    /// disk (by the manifest's own record; no disk probe), their bytes, and how many are skipped as queued.
    /// A file Verify already found gone is priced at zero: there are no bytes to free.</summary>
    public static (int Files, long Bytes, int Queued) Preview(LibraryManifest m, IEnumerable<GameFile> files, IBackupLayout? layout = null)
    {
        int n = 0, q = 0; long bytes = 0;
        foreach (var f in files)
        {
            if (m.Downloads.Contains(f.GameGogId, f.FileKey)) { q++; continue; }
            if (!BackupScope.HoldsBytes(f)) continue;
            n++;
            if (f.State != FileState.Missing) bytes += f.LocalSizeBytes ?? f.ExpectedSizeBytes ?? 0;
            // Only the archived builds Plan will target: one on a drive that is not here is a failure there, not a byte here.
            bytes += OldVersionsOf(m, f).Where(o => layout is null || layout.IsOnline(m.EffectiveRootId(o))).Sum(o => o.LocalSizeBytes ?? 0);
        }
        return (n, bytes, q);
    }

    /// <summary>Step 1 (gate held): which files go, and where they are. A file on a drive the layout has not
    /// bound is a named failure here, so nothing about it is attempted. A withdrawn archive record (a slot GOG
    /// no longer lists) is passed directly like any file; Apply removes it from the archive list.</summary>
    public static DeletePlan Plan(LibraryManifest m, IBackupLayout layout, IEnumerable<GameFile> files)
    {
        var targets = new List<Target>(); int skipped = 0; var failures = new List<string>();
        foreach (var f in files.ToList())
        {
            if (m.Downloads.Contains(f.GameGogId, f.FileKey)) { skipped++; continue; }
            if (!BackupScope.HoldsBytes(f)) continue;
            string? path; string? rootPath;
            // A null RootId RESOLVES to the primary (ResolvePath does the same); RootPath(null) read every such file as "drive not connected".
            try { path = layout.ResolvePath(f); rootPath = layout.RootPath(m.EffectiveRootId(f)); } catch { path = null; rootPath = null; }
            if (path is null || rootPath is null) { failures.Add($"{f.Name}: its drive is not connected"); continue; }

            var olds = new List<(GameFile, string)>();
            foreach (var old in OldVersionsOf(m, f))
            {
                string? op; try { op = layout.ResolvePath(old); } catch { op = null; }
                // An archived build on a drive that is not here is named, like the file itself, never silently skipped.
                if (op is null) { failures.Add($"{old.Name} (old version): its drive is not connected"); continue; }
                olds.Add((old, op));
            }
            // The resumable partial is keyed by identity on the file's own root; HasPartial says whether one exists.
            var part = f.HasPartial ? Download.DownloadEngine.PartialPathFor(rootPath, f) : null;
            targets.Add(new Target(f, path, rootPath, olds, part));
        }
        return new DeletePlan(targets, skipped, failures);
    }

    /// <summary>Step 1 for a WHOLE DRIVE (the Storage page's "Delete files" on removing a folder; sweep 2 #7:
    /// that was a hand loop that left .part files, empty folders and the wrong records behind). Every file
    /// whose bytes sit on <paramref name="rootId"/>, with only the archived builds that are on that drive
    /// too; a retained build whose current file lives elsewhere is a target of its own. Queued files are
    /// NOT skipped here: the drive is leaving, so the copy on it goes and the queue entry stays.</summary>
    public static DeletePlan PlanForRoot(LibraryManifest m, IBackupLayout layout, string rootId)
    {
        var targets = new List<Target>(); var failures = new List<string>();
        string? rootPath; try { rootPath = layout.RootPath(rootId); } catch { rootPath = null; }
        bool OnRoot(GameFile f) => m.EffectiveRootId(f) == rootId && !string.IsNullOrEmpty(f.LocalRelativePath);
        foreach (var item in m.Items)
        {
            var claimed = new HashSet<GameFile>();
            foreach (var f in item.Files.Where(OnRoot).ToList())
            {
                string? path; try { path = layout.ResolvePath(f); } catch { path = null; }
                if (path is null || rootPath is null) { failures.Add($"{f.Name}: its drive is not connected"); continue; }
                var olds = new List<(GameFile, string)>();
                foreach (var old in OldVersionsOf(m, f).Where(OnRoot))
                {
                    string? op; try { op = layout.ResolvePath(old); } catch { op = null; }
                    if (op is not null) { olds.Add((old, op)); claimed.Add(old); }
                }
                var part = f.HasPartial ? Download.DownloadEngine.PartialPathFor(rootPath, f) : null;
                targets.Add(new Target(f, path, rootPath, olds, part));
            }
            foreach (var old in item.OldVersionFiles.Where(o => OnRoot(o) && !claimed.Contains(o)).ToList())
            {
                string? op; try { op = layout.ResolvePath(old); } catch { op = null; }
                if (op is null || rootPath is null) { failures.Add($"{old.Name}: its drive is not connected"); continue; }
                targets.Add(new Target(old, op, rootPath, Array.Empty<(GameFile, string)>(), null));
            }
        }
        return new DeletePlan(targets, 0, failures);
    }

    /// <summary>Step 2 (NO gate): the disk work. Returns one outcome per target; a failure leaves the target's
    /// record for Apply to keep, so it still points at bytes that are still there. <paramref name="onProgress"/>
    /// (targets done, total, bytes freed) is for a caller that paints a long delete.</summary>
    public static List<Outcome> Execute(DeletePlan plan, IDeleteIo? io = null, List<string>? failures = null,
                                        Action<int, int, long>? onProgress = null)
    {
        io ??= RealIo.Instance;
        var outcomes = new List<Outcome>(plan.Targets.Count);
        var dirs = new HashSet<string>(StringComparer.Ordinal);
        long freedSoFar = 0;
        foreach (var t in plan.Targets)
        {
            long bytes = 0; var oldsGone = new List<GameFile>();
            // The archived builds first: they are only findable through this file's key.
            foreach (var (old, opath) in t.OldVersions)
            {
                var r = TryRemove(opath, t.RootPath, old.Name, io, dirs, ref bytes, failures);
                if (r != Removal.Failed) oldsGone.Add(old);
            }
            var res = TryRemove(t.Path, t.RootPath, t.File.Name, io, dirs, ref bytes, failures);
            if (res != Removal.Failed && t.PartPath is not null)
                try { if (io.FileExists(t.PartPath)) io.DeleteFile(t.PartPath); } catch { /* an orphan .part is waste, not loss */ }
            outcomes.Add(new Outcome(t, res != Removal.Failed, res == Removal.WasMissing, bytes, oldsGone));
            if (onProgress is not null) { freedSoFar += bytes; onProgress(outcomes.Count, plan.Targets.Count, freedSoFar); }
        }

        // Folders Grog created that are now empty. Same guards as a reorganize (ReorgRunner): never a top-level
        // category folder, never anything with content.
        foreach (var d in dirs.OrderByDescending(d => d.Length))
            FolderPruner.PruneEmptyChain(d, io);
        return outcomes;
    }

    /// <summary>Step 3 (gate held): the records, for what actually went. Back to never-touched (owner,
    /// 09-13): no local facts, no download history, no verify stamp, no carried strikes. What GOG told us
    /// about the file (version, md5, size, resolved name) stays; those are facts about the file, not our copy.</summary>
    public static Result Apply(LibraryManifest m, DeletePlan plan, IReadOnlyList<Outcome> outcomes, List<string> failures)
    {
        int deleted = 0, missing = 0; long bytes = 0;
        foreach (var o in outcomes)
        {
            var item = m.ItemById(o.Target.File.GameGogId);
            foreach (var old in o.OldVersionsGone) item?.OldVersionFiles.Remove(old);
            bytes += o.Bytes;
            if (!o.Cleared) continue;
            deleted++; if (o.WasMissing) missing++;   // "gone as asked", and "was already gone" is a note on it
            var f = o.Target.File;
            // A retained build that was a target in its own right (PlanForRoot: its current file lives on
            // another drive) has no record to go back to: it leaves the archive list.
            if (f.IsOldVersion) item?.OldVersionFiles.Remove(f);
            else f.ResetLocal();
        }
        return new Result(deleted, bytes, plan.Skipped, missing, failures);
    }

    /// <summary>All three steps under the caller's gate. Tests and the CLI; the App runs Execute off the gate.</summary>
    public static Result Delete(LibraryManifest m, IBackupLayout layout, IEnumerable<GameFile> files, IDeleteIo? io = null)
    {
        var plan = Plan(m, layout, files);
        var failures = new List<string>(plan.Failures);
        var outcomes = Execute(plan, io, failures);
        return Apply(m, plan, outcomes, failures);
    }

    public static IEnumerable<GameFile> OldVersionsOf(LibraryManifest m, GameFile f)
        => m.ItemById(f.GameGogId)?.OldVersionFiles.Where(o => o.FileKey.StartsWith(f.FileKey + "#old:", StringComparison.Ordinal))
           ?? Enumerable.Empty<GameFile>();

    private enum Removal { Deleted, WasMissing, Failed }

    /// <summary>Removes one file's bytes. Deleted, or WasMissing when the file is already gone from a drive that
    /// IS here (a stale record is still a record to clear). Failed when the delete failed OR the whole root is
    /// absent from disk: that is an unplugged drive (the primary binds to the caller's path even when that path
    /// does not exist), and clearing its records would strand every byte and the Old Versions archive on it.</summary>
    private static Removal TryRemove(string path, string rootPath, string name, IDeleteIo io, HashSet<string> dirs,
                                     ref long bytes, List<string>? failures)
    {
        try
        {
            if (!io.FileExists(path))
            {
                if (!io.DirectoryExists(rootPath)) { failures?.Add($"{name}: its drive is not connected"); return Removal.Failed; }
                var d0 = Path.GetDirectoryName(path); if (d0 is not null) dirs.Add(d0);
                return Removal.WasMissing;
            }
            long len = io.FileLength(path); io.DeleteFile(path); bytes += len;
            var dir = Path.GetDirectoryName(path);
            if (dir is not null) dirs.Add(dir);
            return Removal.Deleted;
        }
        catch (Exception ex) { failures?.Add($"{name}: {ex.Message}"); return Removal.Failed; }
    }

    /// <summary>The file-system seam. Tests substitute a fake; the app uses <see cref="RealIo"/>.</summary>
    public interface IDeleteIo : IFolderIo
    {
        bool FileExists(string path);
        long FileLength(string path);
        void DeleteFile(string path);
    }

    public sealed class RealIo : IDeleteIo
    {
        public static readonly RealIo Instance = new();
        public bool FileExists(string path) => File.Exists(path);
        public long FileLength(string path) => new FileInfo(path).Length;
        public void DeleteFile(string path) => File.Delete(path);
        // The folder half is FolderPruner's, so one place owns how an empty folder goes.
        public bool DirectoryIsEmpty(string path) => FolderPruner.RealFolderIo.Instance.DirectoryIsEmpty(path);
        public void DeleteDirectory(string path) => FolderPruner.RealFolderIo.Instance.DeleteDirectory(path);
        public bool DirectoryExists(string path) => FolderPruner.RealFolderIo.Instance.DirectoryExists(path);
    }
}
