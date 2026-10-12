// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Grog.Core.Manifest;
using Grog.Core.Models;

namespace Grog.Core.Verify;

public enum ImportMode
{
    Preview,                 // report only; touch nothing
    Adopt,                   // re-point the manifest to matched files
    AdoptAndReportOrphans,   // adopt + write a list of folders/files that matched nothing
}

public sealed record ImportFileMatch(string DiskPath, long Size, string FileKey, string EntryName, FileKind Kind);

public sealed record MatchedFolder(
    string Path, long GogId, string Title, string Reason,
    IReadOnlyList<ImportFileMatch> Files, int UnmatchedFileCount);

public sealed record AmbiguousFolder(string Path, IReadOnlyList<(long GogId, string Title)> Candidates);

public sealed class ImportPlan
{
    public List<MatchedFolder> Matched { get; } = new();
    public List<AmbiguousFolder> Ambiguous { get; } = new();
    public List<string> UnmatchedGameFolders { get; } = new();   // look like game folders, no library match
    public List<string> OrphanFolders { get; } = new();          // scanned but no installer found
    public int FoldersScanned { get; set; }
    public int AdoptedFiles { get; set; }                        // filled in by ApplyAsync
    public List<(long GogId, string FileKey)> AdoptedKeys { get; } = new();

    public int MatchedFileCount => Matched.Sum(m => m.Files.Count);
}

public sealed class ImportPlanner
{
    private readonly IManifestStore _manifest;
    public string BackupRoot { get; }
    public event Action<int, int>? Progress;   // folders done, total

    public ImportPlanner(IManifestStore manifest, string backupRoot, string? rootId = null)
    {
        _manifest = manifest;
        BackupRoot = backupRoot;
        RootId = rootId;
    }

    public string? RootId { get; }

    private static readonly string[] InstallerExts = { ".exe", ".bin", ".sh", ".dmg", ".pkg" };

    private static bool IsInstallerFile(string path)
        => InstallerExts.Contains(Path.GetExtension(path).ToLowerInvariant());

    private static bool LooksLikeGameFolder(string dir)
    {
        try
        {
            if (Directory.EnumerateFiles(dir).Any(IsInstallerFile)) return true;
            if (Directory.Exists(Path.Combine(dir, "Games"))) return false;
            return Directory.Exists(Path.Combine(dir, "Extras"));
        }
        catch { return false; }
    }

    private static IEnumerable<string> SafeDirs(string dir)
    {
        try { return Directory.EnumerateDirectories(dir).ToList(); }
        catch { return Array.Empty<string>(); }
    }

    public List<string> FindCandidateFolders(string root, int maxDepth = 2)
    {
        var found = new List<string>();
        void Visit(string dir, int depth)
        {
            if (LooksLikeGameFolder(dir)) { found.Add(dir); return; }   // game folder -> record, don't descend
            if (depth >= maxDepth) return;                              // depth cap
            foreach (var sub in SafeDirs(dir))
                if (!IsGrogInternalFolder(Path.GetFileName(sub))) Visit(sub, depth + 1);
        }
        if (Directory.Exists(root)) Visit(root, 0);
        return found;
    }

    internal static bool IsGrogInternalFolder(string name)
        => string.Equals(name, Grog.Core.Download.DownloadEngine.OldVersionsFolder, StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, ".grog-tmp", StringComparison.OrdinalIgnoreCase);

    internal static bool IsGrogInternalFile(string dir, string path)
    {
        if (path.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) return true;
        var rel = Path.GetRelativePath(dir, path);
        var segs = rel.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < segs.Length - 1; i++) if (IsGrogInternalFolder(segs[i])) return true;
        return false;
    }

    // matching helpers ----

    internal static string NormAlnum(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = ConvertRomanNumerals(s);
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.ToLowerInvariant())
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
        return sb.ToString();
    }

    internal static string ConvertRomanNumerals(string s)
        => System.Text.RegularExpressions.Regex.Replace(s, @"\b[IVXLCDMivxlcdm]{2,}\b", m =>
        {
            var val = ParseCanonicalRoman(m.Value.ToUpperInvariant());
            return val is > 0 and <= 50 ? val.Value.ToString() : m.Value;
        });

    private static int? ParseCanonicalRoman(string r)
    {
        int Val(char c) => c switch { 'I' => 1, 'V' => 5, 'X' => 10, 'L' => 50, 'C' => 100, 'D' => 500, 'M' => 1000, _ => 0 };
        int total = 0, prev = 0;
        foreach (var c in r)
        {
            int v = Val(c);
            if (v == 0) return null;
            total += v > prev && prev != 0 ? v - 2 * prev : v;
            prev = v;
        }
        return Encode(total) == r ? total : null;

        static string Encode(int n)
        {
            if (n <= 0) return "";
            int[] vals = { 1000, 900, 500, 400, 100, 90, 50, 40, 10, 9, 5, 4, 1 };
            string[] syms = { "M", "CM", "D", "CD", "C", "XC", "L", "XL", "X", "IX", "V", "IV", "I" };
            var sb = new StringBuilder();
            for (int i = 0; i < vals.Length && n > 0; i++)
                while (n >= vals[i]) { sb.Append(syms[i]); n -= vals[i]; }
            return sb.ToString();
        }
    }

    internal static string StripYear(string folderName)
    {
        var s = System.Text.RegularExpressions.Regex.Replace(folderName, @"[\(\[]\s*(19|20)\d{2}\s*[\)\]]", " ");
        return s.Trim();
    }

    internal static string? SlugFromInstallerName(string fileNameNoExt)
    {
        var name = fileNameNoExt.ToLowerInvariant();
        string rest;
        if (name.StartsWith("setup_")) rest = name.Substring("setup_".Length);
        else if (name.StartsWith("gog_")) rest = name.Substring("gog_".Length);
        else return null;

        var parts = rest.Split('_', StringSplitOptions.RemoveEmptyEntries);
        var slugParts = new List<string>();
        foreach (var p in parts)
        {
            if (p.Contains('.') && p[0] is (>= '0' and <= '9')) break;   // dotted version token
            slugParts.Add(p);
        }
        return slugParts.Count > 0 ? string.Join("_", slugParts) : null;
    }

    private static string? SlugFromFolderInstallers(string dir)
    {
        foreach (var f in SafeFiles(dir).Where(IsInstallerFile))
        {
            var slug = SlugFromInstallerName(Path.GetFileNameWithoutExtension(f));
            if (!string.IsNullOrEmpty(slug)) return slug;
        }
        return null;
    }

    private static IEnumerable<string> SafeFiles(string dir)
    {
        try { return Directory.EnumerateFiles(dir).ToList(); }
        catch { return Array.Empty<string>(); }
    }

    // plan ----

    public ImportPlan Plan(string sourceDir, int maxDepth = 2)
    {
        if (!Directory.Exists(sourceDir))
            throw new DirectoryNotFoundException($"Import source not found: {sourceDir}");

        var items = _manifest.Current.Items;
        var bySlug = new Dictionary<string, List<LibraryItem>>();
        var byTitle = new Dictionary<string, List<LibraryItem>>();
        foreach (var it in items)
        {
            Add(bySlug, NormAlnum(it.Slug), it);
            Add(byTitle, NormAlnum(it.Title), it);
        }

        var plan = new ImportPlan();
        var folders = FindCandidateFolders(sourceDir, maxDepth);
        int done = 0;
        foreach (var dir in folders)
        {
            var slug = SlugFromFolderInstallers(dir);
            List<LibraryItem> hits;
            string reason;

            // 1) slug from an installer filename (strongest)
            if (slug is not null && bySlug.TryGetValue(NormAlnum(slug), out var slugHits) && slugHits.Count > 0)
            {
                hits = slugHits; reason = "installer slug";
            }
            else
            {
                // 2) folder name, year-stripped, exact then containment
                var fkey = NormAlnum(StripYear(Path.GetFileName(dir)));
                if (fkey.Length == 0) { plan.OrphanFolders.Add(dir); done++; Progress?.Invoke(done, folders.Count); continue; }
                if (byTitle.TryGetValue(fkey, out var exactT) && exactT.Count > 0) { hits = exactT; reason = "folder name"; }
                else if (bySlug.TryGetValue(fkey, out var exactS) && exactS.Count > 0) { hits = exactS; reason = "folder name"; }
                else
                {
                    var contains = items.Where(i =>
                    {
                        var t = NormAlnum(i.Title);
                        return t.Length > 3 && fkey.Length > 3 && (t.Contains(fkey) || fkey.Contains(t));
                    }).ToList();

                    if (contains.Count > 1)
                    {
                        var inFolder = contains
                            .Where(i => fkey.Contains(NormAlnum(i.Title)))
                            .OrderByDescending(i => NormAlnum(i.Title).Length)
                            .ToList();
                        bool anyContainsFolder = contains.Any(i =>
                        {
                            var t = NormAlnum(i.Title);
                            return !fkey.Contains(t) && t.Contains(fkey);
                        });
                        if (!anyContainsFolder && inFolder.Count >= 1 &&
                            (inFolder.Count == 1 || NormAlnum(inFolder[1].Title).Length < NormAlnum(inFolder[0].Title).Length))
                            contains = new List<LibraryItem> { inFolder[0] };
                    }
                    hits = contains; reason = "folder name (fuzzy)";
                }
            }

            if (hits.Count == 1)
            {
                var item = hits[0];
                var (matches, unmatched) = MatchFilesToEntries(dir, item);
                plan.Matched.Add(new MatchedFolder(dir, item.GogId, item.Title, reason, matches, unmatched));
            }
            else if (hits.Count > 1)
            {
                plan.Ambiguous.Add(new AmbiguousFolder(dir, hits.Select(h => (h.GogId, h.Title)).ToList()));
            }
            else
            {
                plan.UnmatchedGameFolders.Add(dir);
            }
            done++;
            Progress?.Invoke(done, folders.Count);
        }

        plan.FoldersScanned = folders.Count;
        return plan;

        static void Add(Dictionary<string, List<LibraryItem>> map, string key, LibraryItem it)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (!map.TryGetValue(key, out var list)) map[key] = list = new List<LibraryItem>();
            list.Add(it);
        }
    }

    public MatchedFolder? MatchFolderToItem(string folderPath, long gogId)
    {
        var item = _manifest.Current.ItemById(gogId);
        if (item is null) return null;
        var (matches, unmatched) = MatchFilesToEntries(folderPath, item);
        if (matches.Count == 0) return null;
        return new MatchedFolder(folderPath, item.GogId, item.Title, "manual", matches, unmatched);
    }

    private static (List<ImportFileMatch> matches, int unmatched) MatchFilesToEntries(string dir, LibraryItem item)
    {
        var matches = new List<ImportFileMatch>();
        int unmatched = 0;
        var usedKeys = new HashSet<string>();

        List<string> diskFiles;
        try { diskFiles = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Where(f => !IsGrogInternalFile(dir, f)).ToList(); }
        catch { diskFiles = new List<string>(); }

        foreach (var path in diskFiles)
        {
            long size;
            try { size = new FileInfo(path).Length; } catch { continue; }

            var diskName = Path.GetFileName(path);
            GameFile? best = null; bool bestNameMatch = false; long bestDelta = long.MaxValue;
            foreach (var e in item.Files)
            {
                if (usedKeys.Contains(e.FileKey)) continue;
                bool nameMatch = (!string.IsNullOrEmpty(e.ResolvedFileName)
                                  && string.Equals(e.ResolvedFileName, diskName, StringComparison.OrdinalIgnoreCase))
                              || (!string.IsNullOrEmpty(e.LocalRelativePath)
                                  && string.Equals(Path.GetFileName(e.LocalRelativePath!.Replace('\\', '/')), diskName, StringComparison.OrdinalIgnoreCase));
                if (e.ExpectedSizeBytes is not { } es) { if (!nameMatch) continue; es = size; }
                var tol = Math.Max(1L * 1024 * 1024, es / 100);
                if (!nameMatch && Math.Abs(es - size) > tol) continue;
                long delta = Math.Abs(es - size);
                if (best is null || (nameMatch && !bestNameMatch) || (nameMatch == bestNameMatch && delta < bestDelta))
                {
                    best = e; bestNameMatch = nameMatch; bestDelta = delta;
                }
            }

            if (best is not null)
            {
                usedKeys.Add(best.FileKey);
                matches.Add(new ImportFileMatch(path, size, best.FileKey, best.Name, best.Kind));
            }
            else unmatched++;
        }
        return (matches, unmatched);
    }

    // ---- apply ----

    public async Task ApplyAsync(ImportPlan plan, ImportMode mode, CancellationToken ct = default)
    {
        if (mode == ImportMode.Preview) return;

        int adopted = 0;
        using (_manifest.Gate.Enter())   // the whole apply is in-memory; the save follows outside
        {
            _manifest.Gate.AssertHeld();
            var entryByKey = new Dictionary<(long GogId, string FileKey), (LibraryItem i, GameFile f)>();
            foreach (var i in _manifest.Current.Items)
                foreach (var f in i.Files)
                    entryByKey.TryAdd((i.GogId, f.FileKey), (i, f));

            foreach (var folder in plan.Matched)
            {
                foreach (var fm in folder.Files)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!entryByKey.TryGetValue((folder.GogId, fm.FileKey), out var pair)) continue;
                    var entry = pair.f;
                    if (RootId is not null && entry.RootId == RootId && Sync.BackupScope.HoldsBytes(entry)) continue;
                    entry.LocalRelativePath = Download.DownloadEngine.RelPath(BackupRoot, fm.DiskPath);
                    entry.LocalSizeBytes = fm.Size;
                    if (RootId is not null) entry.RootId = RootId;
                    entry.DownloadedAt ??= DateTimeOffset.UtcNow;
                    entry.State = FileState.Present;
                    adopted++;
                    plan.AdoptedKeys.Add((folder.GogId, fm.FileKey));
                }
            }

            foreach (var item in _manifest.Current.Items)
                Sync.LibrarySyncService.RecomputeStatus(item);
        }

        await _manifest.SaveAsync(ct);

        plan.AdoptedFiles = adopted;
    }
}
