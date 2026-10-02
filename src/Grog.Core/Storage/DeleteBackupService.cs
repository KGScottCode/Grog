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
using Grog.Core.Sync;
using Grog.Core.Volumes;

namespace Grog.Core.Storage;

/// <summary>Deletes downloaded backup files to reclaim space (M14/F9). Never touches the GOG account or the
/// manifest entry -- files flip back to NotDownloaded and stay re-downloadable. Dry-run first: callers
/// preview exactly what will be freed before anything is deleted.</summary>
public sealed class DeleteBackupService
{
    private readonly IManifestStore _manifest;
    private readonly IBackupLayout _layout;

    public DeleteBackupService(IManifestStore manifest, IBackupLayout layout)
    {
        _manifest = manifest;
        _layout = layout;
    }

    public sealed record Plan(IReadOnlyList<(GameFile File, string Path, long Bytes)> Files, long TotalBytes)
    {
        public int Count => Files.Count;
    }

    /// <summary>Build a deletion plan (dry-run): the downloaded files matching the selection, with
    /// their on-disk paths and sizes. Nothing is deleted here.</summary>
    public Plan PlanFor(Func<LibraryItem, bool> gameFilter, bool includeInstallers = true, bool includeExtras = true)
    {
        var files = new List<(GameFile, string, long)>();
        long total = 0;
        foreach (var item in _manifest.Current.Items.Where(gameFilter))
            foreach (var f in item.Files)
            {
                // Present-on-disk means Downloaded, Verified, OR UpdateAvailable (BackupScope.IsPresent);
                // filtering on Downloaded alone would miss a fully-verified library.
                if (!BackupScope.IsPresent(f)) continue;
                if (f.Kind == FileKind.Extra && !includeExtras) continue;
                if (f.Kind != FileKind.Extra && !includeInstallers) continue;
                var path = _layout.ResolvePath(f);
                if (path is null || !File.Exists(path)) continue;
                long bytes = Grog.Core.Sync.Rollups.HeldBytesOf(f);
                files.Add((f, path, bytes));
                total += bytes;
            }
        return new Plan(files, total);
    }

    /// <summary>Execute a plan through <see cref="LocalDeletion"/>, THE delete (sweep 2 #7: this was a hand
    /// loop that cleared two fields, left RootId / LocalRelativePath / the verify stamp pointing at nothing,
    /// kept the file's Old Versions and .part, never pruned the folders, and raced a queued download).
    /// Plan and Apply hold the manifest gate; the disk work does not. Returns files removed and bytes freed.</summary>
    public async Task<(int Deleted, long BytesFreed)> ExecuteAsync(Plan plan, CancellationToken ct = default)
    {
        var r = await ExecuteDetailedAsync(plan, ct);
        return (r.Deleted, r.Bytes);
    }

    public async Task<LocalDeletion.Result> ExecuteDetailedAsync(Plan plan, CancellationToken ct = default)
    {
        var files = plan.Files.Select(x => x.File).ToList();
        LocalDeletion.DeletePlan dp;
        using (_manifest.Gate.Enter()) dp = LocalDeletion.Plan(_manifest.Current, _layout, files);
        var failures = new List<string>(dp.Failures);
        var outcomes = await Task.Run(() => LocalDeletion.Execute(dp, null, failures), ct);
        LocalDeletion.Result result;
        using (_manifest.Gate.Enter())
        {
            result = LocalDeletion.Apply(_manifest.Current, dp, outcomes, failures);
            foreach (var id in outcomes.Select(o => o.Target.File.GameGogId).Distinct())
                if (_manifest.Current.ItemById(id) is { } item) LibrarySyncService.RecomputeStatus(item);
        }
        await _manifest.SaveAsync(ct);
        return result;
    }
}
