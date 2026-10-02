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

/// <summary>What import does with matches. Chosen BEFORE the scan runs.</summary>
public enum ImportMode
{
    Preview,                 // report only; touch nothing
    Adopt,                   // re-point the manifest to matched files
    AdoptAndReportOrphans,   // adopt + write a list of folders/files that matched nothing
}

/// <summary>One on-disk file matched to a manifest file entry.</summary>
public sealed record ImportFileMatch(string DiskPath, long Size, string FileKey, string EntryName, FileKind Kind);

/// <summary>A folder confidently matched to a library item, with its adoptable files.</summary>
public sealed record MatchedFolder(
    string Path, long GogId, string Title, string Reason,
    IReadOnlyList<ImportFileMatch> Files, int UnmatchedFileCount);

/// <summary>A folder that matched more than one library item (needs a human).</summary>
public sealed record AmbiguousFolder(string Path, IReadOnlyList<(long GogId, string Title)> Candidates);

/// <summary>The result of scanning a source tree against the manifest. Pure; adoption is separate.</summary>
public sealed class ImportPlan
{
    public List<MatchedFolder> Matched { get; } = new();
    public List<AmbiguousFolder> Ambiguous { get; } = new();
    public List<string> UnmatchedGameFolders { get; } = new();   // look like game folders, no library match
    public List<string> OrphanFolders { get; } = new();          // scanned but no installer found
    public int FoldersScanned { get; set; }
    public int AdoptedFiles { get; set; }                        // filled in by ApplyAsync
    /// <summary>(09-19) The records ApplyAsync re-pointed, so the run can verify exactly those.</summary>
    public List<(long GogId, string FileKey)> AdoptedKeys { get; } = new();

    public int MatchedFileCount => Matched.Sum(m => m.Files.Count);
}

/// <summary>
/// Folder-first importer for adopting a pre-existing GOG backup: walks a chosen folder to a small depth,
/// stops at anything that looks like a game folder, and matches it to a library item by installer slug then
/// year-stripped folder name. ApplyAsync re-points the manifest; nothing on disk is moved or deleted.
/// </summary>
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

    /// <summary>Backup root adopted files are stamped with (capacity tracking); null leaves RootId untouched.</summary>
    public string? RootId { get; }

    private static readonly string[] InstallerExts = { ".exe", ".bin", ".sh", ".dmg", ".pkg" };

    private static bool IsInstallerFile(string path)
        => InstallerExts.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>A folder "is a game folder" if it directly contains at least one installer-looking file, or
    /// carries Grog's own <c>Extras</c> subfolder (09-17: a game whose only backed-up files are extras has no
    /// installer at its top level, so re-adding a drive never scanned it and 4 extras-only games stayed
    /// un-adopted; the depth cap stopped the walk exactly there).</summary>
    private static bool LooksLikeGameFolder(string dir)
    {
        try
        {
            if (Directory.EnumerateFiles(dir).Any(IsInstallerFile)) return true;
            // (09-19, walk pass 3) A BACKUP ROOT has a top-level Extras folder too, once a separate extras tree
            // has ever been applied (FolderPruner never removes it). The root then read as ONE game folder:
            // "no library match", nothing under Games was scanned, 0 adopted. A root is not a game.
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

    /// <summary>Depth-capped walk from the picked folder; never descends into a folder that already looks
    /// like a game folder.</summary>
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

    /// <summary>Folders Grog itself makes that never hold a CURRENT file: the archive of superseded builds and
    /// the partial-download folder. Import walked into both (sweep 2 #11): an old build sits within the size
    /// tolerance of the new one and was adopted as the current file, and a .part as a finished download.</summary>
    internal static bool IsGrogInternalFolder(string name)
        => string.Equals(name, Grog.Core.Download.DownloadEngine.OldVersionsFolder, StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, ".grog-tmp", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="path"/> (under <paramref name="dir"/>) is inside an internal folder or
    /// is itself a partial.</summary>
    internal static bool IsGrogInternalFile(string dir, string path)
    {
        if (path.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) return true;
        var rel = Path.GetRelativePath(dir, path);
        var segs = rel.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < segs.Length - 1; i++) if (IsGrogInternalFolder(segs[i])) return true;
        return false;
    }

    // ---- matching helpers ----

    /// <summary>Lowercase alnum-only key ("Baldur's Gate 1" == "baldurs_gate_1"); canonical roman numerals
    /// become arabic first so "Civilization III" matches "Civilization 3".</summary>
    internal static string NormAlnum(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = ConvertRomanNumerals(s);
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.ToLowerInvariant())
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
        return sb.ToString();
    }

    /// <summary>Replaces canonical roman-numeral words (length >= 2, value <= 50) with arabic. Length-1
    /// tokens (esp. "I") stay untouched to avoid mangling names like "I, Robot".</summary>
    internal static string ConvertRomanNumerals(string s)
        => System.Text.RegularExpressions.Regex.Replace(s, @"\b[IVXLCDMivxlcdm]{2,}\b", m =>
        {
            var val = ParseCanonicalRoman(m.Value.ToUpperInvariant());
            return val is > 0 and <= 50 ? val.Value.ToString() : m.Value;
        });

    /// <summary>Parses a roman numeral, returning its value only if canonical (re-encoding yields the same
    /// string); rejects forms like "IIII".</summary>
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

    /// <summary>Strips a "(1998)" or "[1998]" year token from a folder name.</summary>
    internal static string StripYear(string folderName)
    {
        var s = System.Text.RegularExpressions.Regex.Replace(folderName, @"[\(\[]\s*(19|20)\d{2}\s*[\)\]]", " ");
        return s.Trim();
    }

    /// <summary>Pulls the slug from a GOG installer filename ("setup_syberia_2_2.0.0.13.exe" -> "syberia_2"),
    /// breaking at the dotted version token; bare sequel numbers stay or sequels collide with their originals.</summary>
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

    // ---- plan ----

    /// <summary>Scans the source tree and classifies each candidate folder against the manifest. Pure.</summary>
    public ImportPlan Plan(string sourceDir, int maxDepth = 2)
    {
        if (!Directory.Exists(sourceDir))
            throw new DirectoryNotFoundException($"Import source not found: {sourceDir}");

        var items = _manifest.Current.Items;
        // Pre-index by normalized slug + title for cheap lookups.
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

                    // Prefer the most-specific candidate: the longest title fully contained in the folder key
                    // (so "DOOM 3: Phobos" beats "DOOM 3" for a "Doom 3 Phobos" folder).
                    if (contains.Count > 1)
                    {
                        // Collapse only when every candidate is contained in the folder key. If any candidate
                        // instead contains the folder key (folder "Doom 3 BFG" vs "Doom 3 BFG Edition") it is
                        // genuinely ambiguous -- fall through so the user disambiguates.
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

    /// <summary>Builds a matched folder from a user's manual pick ("Find match"); null if nothing pairs.</summary>
    public MatchedFolder? MatchFolderToItem(string folderPath, long gogId)
    {
        var item = _manifest.Current.ItemById(gogId);
        if (item is null) return null;
        var (matches, unmatched) = MatchFilesToEntries(folderPath, item);
        if (matches.Count == 0) return null;
        return new MatchedFolder(folderPath, item.GogId, item.Title, "manual", matches, unmatched);
    }

    /// <summary>Matches a game folder's files to the item's entries by size within tolerance; returns
    /// confident matches plus a count of files that matched no entry.</summary>
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

            // Prefer a filename match over pure size: near-equal-size installer parts would otherwise
            // cross-adopt by enumeration order. Exact CDN-filename match is authoritative; size breaks ties.
            var diskName = Path.GetFileName(path);
            GameFile? best = null; bool bestNameMatch = false; long bestDelta = long.MaxValue;
            foreach (var e in item.Files)
            {
                if (usedKeys.Contains(e.FileKey)) continue;
                // A NAME the record itself carries is authoritative: the CDN file name, or the leaf of the path
                // the record last held (a forgotten drive re-added, 09-17: the record still says
                // "Extras/pst_avatars.zip"). GOG's ExpectedSizeBytes is rounded (tiny extras report 1 MB, a
                // 2.7 GB part reported 2.68 GB), so a size-only test dropped 5 of 44 files on re-add.
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

    /// <summary>Carries out the plan: Preview does nothing; Adopt re-points matched entries to the found
    /// files and recomputes item status. Never deletes.</summary>
    public async Task ApplyAsync(ImportPlan plan, ImportMode mode, CancellationToken ct = default)
    {
        if (mode == ImportMode.Preview) return;

        // Keyed by (game, FileKey), never FileKey alone: FileKey is GOG's download path and GOG serves one
        // extra under several products (Ultima Underworld I and II share Ultima_Underworld_1_2_QRC.zip, same
        // key). A manifest-wide first-wins map adopted the FIRST game's entry twice and left the second game's
        // record Missing (owner-hit 09-04: 6 of 142 re-adopted files stayed "gone from disk").
        // Still duplicate-tolerant within a game (GOG omits manualUrl on some pairs).
        int adopted = 0;
        using (_manifest.Gate.Enter())   // (manifest gate 09-08) the whole apply is in-memory; the save follows outside
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
                    // (09-15 review) A record that ALREADY holds bytes on this root is not an adoption: re-adding a
                    // detached drive's folder must not demote Verified to Present or erase Sync's Outdated /
                    // Verify's Corrupt signal (invariant: those two own their signals).
                    if (RootId is not null && entry.RootId == RootId && Sync.BackupScope.HoldsBytes(entry)) continue;
                    // Store the path relative to the backup root (may be an existing in-root file).
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
