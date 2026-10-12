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

public static class LocalDeletion
{
    public sealed record Result(int Deleted, long Bytes, int Skipped, int Missing, List<string> Failures)
    {
        public static readonly Result Empty = new(0, 0, 0, 0, new());
    }

    public sealed record Target(GameFile File, string Path, string RootPath, IReadOnlyList<(GameFile Old, string Path)> OldVersions, string? PartPath);

    public sealed record DeletePlan(IReadOnlyList<Target> Targets, int Skipped, List<string> Failures);

    public sealed record Outcome(Target Target, bool Cleared, bool WasMissing, long Bytes, List<GameFile> OldVersionsGone);

    public static (int Files, long Bytes, int Queued) Preview(LibraryManifest m, IEnumerable<GameFile> files, IBackupLayout? layout = null)
    {
        int n = 0, q = 0; long bytes = 0;
        foreach (var f in files)
        {
            if (m.Downloads.Contains(f.GameGogId, f.FileKey)) { q++; continue; }
            if (!BackupScope.HoldsBytes(f)) continue;
            n++;
            if (f.State != FileState.Missing) bytes += f.LocalSizeBytes ?? f.ExpectedSizeBytes ?? 0;
            // Only the archived builds Plan will target
            bytes += OldVersionsOf(m, f).Where(o => layout is null || layout.IsOnline(m.EffectiveRootId(o))).Sum(o => o.LocalSizeBytes ?? 0);
        }
        return (n, bytes, q);
    }

    public static DeletePlan Plan(LibraryManifest m, IBackupLayout layout, IEnumerable<GameFile> files)
    {
        var targets = new List<Target>(); int skipped = 0; var failures = new List<string>();
        foreach (var f in files.ToList())
        {
            if (m.Downloads.Contains(f.GameGogId, f.FileKey)) { skipped++; continue; }
            if (!BackupScope.HoldsBytes(f)) continue;
            string? path; string? rootPath;
            try { path = layout.ResolvePath(f); rootPath = layout.RootPath(m.EffectiveRootId(f)); } catch { path = null; rootPath = null; }
            if (path is null || rootPath is null) { failures.Add($"{f.Name}: its drive is not connected"); continue; }

            var olds = new List<(GameFile, string)>();
            foreach (var old in OldVersionsOf(m, f))
            {
                string? op; try { op = layout.ResolvePath(old); } catch { op = null; }
                if (op is null) { failures.Add($"{old.Name} (old version): its drive is not connected"); continue; }
                olds.Add((old, op));
            }
            var part = f.HasPartial ? Download.DownloadEngine.PartialPathFor(rootPath, f) : null;
            targets.Add(new Target(f, path, rootPath, olds, part));
        }
        return new DeletePlan(targets, skipped, failures);
    }

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
            // The archived builds first
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

        // Folders Grog created that are now empty
        foreach (var d in dirs.OrderByDescending(d => d.Length))
            FolderPruner.PruneEmptyChain(d, io);
        return outcomes;
    }

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
            if (f.IsOldVersion) item?.OldVersionFiles.Remove(f);
            else f.ResetLocal();
        }
        return new Result(deleted, bytes, plan.Skipped, missing, failures);
    }

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
        public bool DirectoryIsEmpty(string path) => FolderPruner.RealFolderIo.Instance.DirectoryIsEmpty(path);
        public void DeleteDirectory(string path) => FolderPruner.RealFolderIo.Instance.DeleteDirectory(path);
        public bool DirectoryExists(string path) => FolderPruner.RealFolderIo.Instance.DirectoryExists(path);
    }
}
