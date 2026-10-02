// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Storage;
using Grog.Core.Volumes;

namespace Grog.Core.Preservation;

/// <summary>Preservation reporting and integrity operations over the manifest and layout: health summary,
/// delisted/at-risk report, serial-key export, zip-integrity checking, safe orphan handling (move-not-delete).</summary>
public sealed class PreservationService
{
    private readonly IManifestStore _store;
    private readonly IBackupLayout _layout;

    public PreservationService(IManifestStore store, IBackupLayout layout)
    {
        _store = store;
        _layout = layout;
    }

    private LibraryManifest M => _store.Current;

    // ---- health summary --------------------------------------------------------------------

    public HealthSummary Health()
    {
        var h = new HealthSummary { TotalItems = M.Items.Count };
        foreach (var i in M.Items)
        {
            switch (i.Status)
            {
                case BackupStatus.Complete: h.Complete++; break;
                case BackupStatus.Partial: h.Partial++; break;
                case BackupStatus.Outdated: h.Outdated++; break;
                case BackupStatus.NotBackedUp: h.NotBackedUp++; break;
                case BackupStatus.Corrupt: h.CorruptItems++; break;
            }
        }

        foreach (var f in M.Items.SelectMany(i => i.Files))
        {
            if (f.State == FileState.Corrupt) h.CorruptFiles++;
            if (f.State == FileState.Missing) h.MissingFiles++;
            if (f.LocalSizeBytes is { } sz) h.BytesOnDisk += sz;
        }

        // Per-root coverage (online vs offline) and delisted count.
        foreach (var root in M.Roots)
        {
            if (root.State == RootState.Detached) continue;   // on the shelf by choice: not an "offline drive"
            var online = _layout.IsOnline(root.Id);
            var files = M.Items.SelectMany(i => i.Files).Count(f => M.EffectiveRootId(f) == root.Id);
            h.RootCoverage.Add(new RootCoverage(root.Label, online, files));
            if (!online) h.OfflineRoots++;
        }

        h.Delisted = M.Items.Count(IsDelisted);
        return h;
    }

    // ---- delisted / at-risk ----------------------------------------------------------------

    /// <summary>A product is "delisted / at-risk" when flagged by sync, or when a game/mod has no downloadable
    /// files recorded -- anything the user should double-check is safely backed up.</summary>
    public static bool IsDelisted(LibraryItem i) =>
        i.IsDelisted ||
        (i.Type is ProductType.Game or ProductType.Mod && i.Files.Count == 0);

    public IReadOnlyList<LibraryItem> DelistedItems() => M.Items.Where(IsDelisted).ToList();

    // ---- serial keys -----------------------------------------------------------------------

    public IReadOnlyList<(string title, string serial)> SerialKeys() =>
        M.Items.Where(i => !string.IsNullOrWhiteSpace(i.SerialKey))
               .Select(i => (i.Title, Grog.Core.Api.GameDetailsParser.CleanSerial(i.SerialKey) ?? i.SerialKey!))
               .OrderBy(x => x.Item1).ToList();

    /// <summary>Writes serials to a text file in the config dir; returns the path (or null if none).</summary>
    public async Task<string?> ExportSerialsAsync(string configDir, CancellationToken ct = default)
    {
        var serials = SerialKeys();
        if (serials.Count == 0) return null;
        var path = Path.Combine(configDir, "serial_keys.txt");
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("GOG serial keys -- exported by Grog");
        sb.AppendLine(new string('=', 40));
        sb.AppendLine();
        foreach (var (title, serial) in serials)
        {
            sb.AppendLine(title);
            foreach (var line in serial.Split('\n'))
                sb.AppendLine("    " + line);
            sb.AppendLine();
        }
        await File.WriteAllTextAsync(path, sb.ToString(), ct);
        return path;
    }

    // ---- zip integrity ---------------------------------------------------------------------

    /// <summary>Opens each local .zip extra and checks its central directory / entries are readable.
    /// Flags unreadable archives as Corrupt. Installers (.exe/.sh/.pkg/.bin) are skipped -- not zips.</summary>
    public async Task<ZipCheckResult> CheckZipsAsync(CancellationToken ct = default)
    {
        var result = new ZipCheckResult();
        var files = M.Items.SelectMany(i => i.Files)
            .Where(f => f.Kind == FileKind.Extra && (f.LocalRelativePath?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ?? false))
            .ToList();

        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            if (!_layout.IsOnline(M.EffectiveRootId(f))) { result.Skipped++; continue; }
            var path = _layout.ResolvePath(f);
            if (path is null || !File.Exists(path)) { result.Skipped++; continue; }

            result.Checked++;
            bool ok = false;
            try
            {
                ok = await Task.Run(() =>
                {
                    try
                    {
                        using var zip = ZipFile.OpenRead(path);
                        // Touch every entry header to force the central directory to be parsed.
                        foreach (var _ in zip.Entries) { }
                        return true;
                    }
                    catch (InvalidDataException) { return false; }   // not a valid zip / truncated
                    catch (IOException) { return false; }            // unreadable / locked / partial
                }, ct);
            }
            catch (OperationCanceledException) { throw; }            // cancellation is not corruption
            catch (Exception) { ok = false; }                        // any other read failure = bad file

            if (ok) result.Ok++;
            else
            {
                _store.Mutate(_ => f.State = FileState.Corrupt);   // (manifest gate 09-08) per file: the zip read above awaits
                result.Bad++;

                result.BadFiles.Add(f.LocalRelativePath!);
            }
        }
        if (result.Bad > 0) await _store.SaveAsync(ct);
        return result;
    }

    // ---- orphans ---------------------------------------------------------------------------

    /// <summary>Finds files on disk under online roots that no manifest entry claims. Optionally moves
    /// them into &lt;root&gt;/.grog/orphaned/ (never hard-deletes). Dry-run by default.</summary>
    public OrphanResult HandleOrphans(bool move)
    {
        var result = new OrphanResult();
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // OldVersionFiles too: the archive is recorded there, not in Files. Left out, every retained build read
        // as an orphan and --move carried the irreplaceable Old Versions/ archive away (sweep 2 #27).
        foreach (var f in M.Items.SelectMany(i => i.Files.Concat(i.OldVersionFiles)))
        {
            var p = _layout.ResolvePath(f);
            if (p is not null) known.Add(Path.GetFullPath(p));
        }

        foreach (var root in M.Roots.Where(r => _layout.IsOnline(r.Id)))
        {
            var rootPath = _layout.RootPath(root.Id);
            if (rootPath is null) continue;
            foreach (var path in EnumerateContentFiles(rootPath))
            {
                if (known.Contains(Path.GetFullPath(path))) continue;
                result.Orphans.Add(path);
                if (move)
                {
                    var destDir = Path.Combine(rootPath, OrphanedFolder);
                    Directory.CreateDirectory(destDir);
                    var dest = Path.Combine(destDir, Path.GetFileName(path));
                    if (File.Exists(dest)) dest = Path.Combine(destDir, $"{Guid.NewGuid():N}_{Path.GetFileName(path)}");
                    try { File.Move(path, dest); result.Moved++; } catch { /* skip locked/failed */ }
                }
            }
        }
        return result;
    }

    /// <summary>Where --move puts orphans. Itself never scanned, or a second run moves the first run's orphans.</summary>
    public const string OrphanedFolder = "_Grog_Orphaned";

    /// <summary>Content files under a root: never Grog's own config dir, and never the places Grog keeps things
    /// the manifest's Files list does not name -- partial downloads (.grog-tmp, .part), the Old Versions
    /// archive (an untracked build there is still nothing to sweep up), the cloud-save archive, the root
    /// marker, and the orphan folder itself.</summary>
    private static IEnumerable<string> EnumerateContentFiles(string rootPath)
    {
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories); }
        catch { yield break; }
        foreach (var f in files)
            if (!IsGrogOwned(rootPath, f))
                yield return f;
    }

    internal static bool IsGrogOwned(string rootPath, string file)
    {
        if (file.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) return true;
        var segs = Path.GetRelativePath(rootPath, file)
            .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < segs.Length - 1; i++)
        {
            var s = segs[i];
            if (string.Equals(s, GrogPaths.ConfigDirName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, ".grog-tmp", StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, Grog.Core.Download.DownloadEngine.OldVersionsFolder, StringComparison.OrdinalIgnoreCase)
                || (i == 0 && string.Equals(s, OrphanedFolder, StringComparison.OrdinalIgnoreCase))
                || (i == 0 && string.Equals(s, "Cloud Saves", StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return false;
    }
}

public sealed class HealthSummary
{
    public int TotalItems { get; set; }
    public int Complete { get; set; }
    public int Partial { get; set; }
    public int Outdated { get; set; }
    public int NotBackedUp { get; set; }
    public int CorruptItems { get; set; }
    public int CorruptFiles { get; set; }
    public int MissingFiles { get; set; }
    public int Delisted { get; set; }
    public int OfflineRoots { get; set; }
    public long BytesOnDisk { get; set; }
    public List<RootCoverage> RootCoverage { get; } = new();
}

public sealed record RootCoverage(string Label, bool Online, int Files);

public sealed class ZipCheckResult
{
    public int Checked { get; set; }
    public int Ok { get; set; }
    public int Bad { get; set; }
    public int Skipped { get; set; }
    public List<string> BadFiles { get; } = new();
}

public sealed class OrphanResult
{
    public List<string> Orphans { get; } = new();
    public int Moved { get; set; }
}
