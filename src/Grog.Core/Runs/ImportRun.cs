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
using Grog.Core.Verify;
using Grog.Core.Volumes;

namespace Grog.Core.Runs;

public enum ImportRunStatus
{
    Deferred,
    Unreachable,
    Previewed,
    Adopted,
    Failed,
}

public sealed record ImportRunResult(BackupRoot Root, ImportRunStatus Status, ImportPlan? Plan, int Adopted,
                                     IReadOnlyList<string> Problems, Exception? Error = null)
{
    public bool NeedsHuman => Problems.Count > 0;
    public bool MatchedExisting { get; init; }
}

public static class ImportRun
{
    public sealed class Options
    {
        public Action<int, int>? Progress { get; init; }
        public int MaxDepth { get; init; } = 2;
        public bool VerifyAdopted { get; init; } = true;
        public Func<GameFile, CancellationToken, Task<string?>>? FetchCurrentServerMd5 { get; init; }
        public bool FetchMissingChecksums { get; init; }
    }

    public static async Task<ImportRunResult> AddAndImportAsync(IManifestStore store, string path, ImportMode mode,
                                                                IBackupHost host, CancellationToken ct, Options? options = null)
    {
        if (MatchExistingRoot(store, path, options?.MaxDepth ?? 2) is { } matched)
        {
            bool samePath = !string.IsNullOrEmpty(matched.PathHint) && string.Equals(Path.GetFullPath(matched.PathHint), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);

            if (!samePath) new VolumeService(store).RepointRoot(matched.Id, path);

            store.Mutate(_ => { if (matched.State is RootState.Detached or RootState.Lost) matched.State = RootState.Offline; });
            await store.SaveAsync(ct);

            if (!samePath) host.Log($"{matched.Label} is at {Path.GetFullPath(path)} now: its files matched, nothing re-downloads.");
            var r = await ImportExistingRootAsync(store, matched, path, mode, host, ct, options);
            return r with { MatchedExisting = !samePath };
        }

        var root = new VolumeService(store).AddRoot(path);
        bool defer = store.Read(m => PendingImports.MustDefer(m));
        if (defer)
        {
            store.Mutate(_ => PendingImports.Mark(root));
            await store.SaveAsync(ct);
            host.Log("Added storage. Grog will look for existing backups in it after your first library scan.");
            return new ImportRunResult(root, ImportRunStatus.Deferred, null, 0, Array.Empty<string>());
        }
        await store.SaveAsync(ct);   // the registration stands even if the import below throws
        return await ImportExistingRootAsync(store, root, path, mode, host, ct, options);
    }

    public static BackupRoot? MatchExistingRoot(IManifestStore store, string path, int maxDepth = 2)
    {
        var full = Path.GetFullPath(path);
        if (!Directory.Exists(full)) return null;
        var m = store.Current;
        if (m.Items.Count == 0 || m.Roots.Count == 0) return null;
        ImportPlan plan;
        try { plan = new ImportPlanner(store, full).Plan(full, maxDepth); }
        catch { return null; }
        if (plan.MatchedFileCount == 0) return null;

        var tally = new Dictionary<string, int>(StringComparer.Ordinal);
        int matchedKnown = 0;
        using (store.Gate.Enter())
        {
            var byKey = new Dictionary<(long, string), GameFile>();
            foreach (var it in m.Items) foreach (var f in it.Files) byKey[(it.GogId, f.FileKey)] = f;
            foreach (var folder in plan.Matched)
                foreach (var fm in folder.Files)
                {
                    if (!byKey.TryGetValue((folder.GogId, fm.FileKey), out var f) || !Sync.BackupScope.HoldsBytes(f)) continue;
                    var rootId = m.EffectiveRootId(f);
                    if (rootId is null || string.IsNullOrEmpty(f.LocalRelativePath)) continue;
                    var rel = Download.DownloadEngine.RelPath(full, fm.DiskPath).Replace('\\', '/');
                    if (!string.Equals(rel, f.LocalRelativePath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)) continue;
                    matchedKnown++;
                    tally[rootId] = tally.TryGetValue(rootId, out var n) ? n + 1 : 1;
                }
        }
        if (tally.Count == 0) return null;

        var (bestId, best) = tally.OrderByDescending(kv => kv.Value).Select(kv => (kv.Key, kv.Value)).First();

        if (best * 2 < plan.MatchedFileCount) return null;   // most of the folder is something else

        var root = m.Roots.FirstOrDefault(r => r.Id == bestId);

        if (root is null) return null;
        if (!string.IsNullOrEmpty(root.PathHint) && !string.Equals(Path.GetFullPath(root.PathHint), full, StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(root.PathHint)) return null;
        return root;
    }

    public static async Task<IReadOnlyList<ImportRunResult>> RunPendingAsync(IManifestStore store, IBackupHost host,
                                                                             CancellationToken ct, Options? options = null)
    {
        var results = new List<ImportRunResult>();
        var due = store.Read(m => PendingImports.Runnable(m));
        foreach (var root in due)
        {
            ct.ThrowIfCancellationRequested();

            if (!Directory.Exists(root.PathHint))
            {
                host.Log($"{root.Label} is not reachable; its import will run after the next scan.");
                results.Add(new ImportRunResult(root, ImportRunStatus.Unreachable, null, 0, Array.Empty<string>()));
                continue;
            }
            store.Mutate(_ => PendingImports.Clear(root));
            var r = await ImportExistingRootAsync(store, root, root.PathHint, ImportMode.Adopt, host, ct, options);
            if (r.Status == ImportRunStatus.Failed)
                host.Log($"Couldn't scan {root.Label} for existing backups: {r.Error!.Message}", isError: true);
            await store.SaveAsync(ct);   // persists the cleared flag even when the plan adopted nothing
            results.Add(r);
        }
        return results;
    }

    public static async Task<ImportRunResult> ImportExistingRootAsync(IManifestStore store, BackupRoot root, string path,
                                                                      ImportMode mode, IBackupHost host, CancellationToken ct,
                                                                      Options? options = null)
    {
        options ??= new Options();
        try
        {
            host.Log($"Scanning {root.Label} for existing backups…");
            var planner = new ImportPlanner(store, root.PathHint, root.Id);
            if (options.Progress is { } progress) planner.Progress += progress;
            var plan = planner.Plan(path, options.MaxDepth);
            ct.ThrowIfCancellationRequested();

            var problems = new List<string>();
            foreach (var a in plan.Ambiguous)
                problems.Add($"{a.Path}: could be {string.Join(", ", a.Candidates.Select(c => c.Title))}");
            foreach (var u in plan.UnmatchedGameFolders)
                problems.Add($"{u}: matched nothing in your library");

            if (mode == ImportMode.Preview)
            {
                host.Log($"Preview: {plan.Matched.Count} matched, {plan.Ambiguous.Count} ambiguous, {plan.UnmatchedGameFolders.Count} unmatched. Nothing changed.");
                return new ImportRunResult(root, ImportRunStatus.Previewed, plan, 0, problems);
            }

            if (plan.MatchedFileCount > 0) await planner.ApplyAsync(plan, mode, ct);
            host.Log($"Adopted {plan.AdoptedFiles} file(s) into {root.Label}.");
            if (plan.AdoptedKeys.Count > 0 && options.VerifyAdopted)
                await VerifyAdoptedAsync(store, root, path, plan, host, options, ct).ConfigureAwait(false);
            if (problems.Count > 0)
                host.Log($"{problems.Count} folder(s) in {root.Label} need a Find match.");
            return new ImportRunResult(root, ImportRunStatus.Adopted, plan, plan.AdoptedFiles, problems);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new ImportRunResult(root, ImportRunStatus.Failed, null, 0, Array.Empty<string>(), ex);
        }
    }

    private static async Task VerifyAdoptedAsync(IManifestStore store, BackupRoot root, string path, ImportPlan plan,
                                                 IBackupHost host, Options options, CancellationToken ct)
    {
        host.Log($"Verifying {plan.AdoptedKeys.Count} adopted file(s) on {root.Label}…");
        var m = store.Current;
        var primaryPath = m.Roots.FirstOrDefault(r => r.Id == m.PrimaryRootId)?.PathHint;
        var request = VerifyRequest.ForItems(plan.AdoptedKeys.Select(k => k.GogId).Distinct(), Grog.Core.Verify.VerifyMode.Unverified) with
        {
            Progress = options.Progress is { } p ? vp => p(vp.Done, vp.Total) : null,
            FetchCurrentServerMd5 = options.FetchCurrentServerMd5,
            FetchMissingChecksums = options.FetchMissingChecksums && options.FetchCurrentServerMd5 is not null,
        };
        var v = (await VerifyRun.RunAsync(store, string.IsNullOrEmpty(primaryPath) ? path : primaryPath, request, host, ct)
                                .ConfigureAwait(false)).Verify;
        host.Log($"Verified adopted files on {root.Label}: {v.Verified} verified, {v.SizeOnlyOk} size-only, "
               + $"{v.Corrupt} corrupt, {v.Missing} missing.", isError: v.Corrupt > 0 || v.Missing > 0);
    }
}
