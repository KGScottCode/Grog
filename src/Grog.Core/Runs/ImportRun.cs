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

/// <summary>How an import of one root ended.</summary>
public enum ImportRunStatus
{
    /// <summary>The root is registered and flagged <see cref="BackupRoot.PendingImport"/>: the library has no
    /// items yet, so there is nothing to match against. Runs after the first scan.</summary>
    Deferred,
    /// <summary>A pending root whose folder was not reachable: left flagged for the next attempt.</summary>
    Unreachable,
    /// <summary>Planned only (<see cref="ImportMode.Preview"/>); nothing changed.</summary>
    Previewed,
    /// <summary>Planned and confident matches adopted (possibly zero).</summary>
    Adopted,
    /// <summary>The plan or apply threw; <see cref="ImportRunResult.Error"/> says why. A pending root's flag is
    /// already cleared by then (a failure is logged, not retried forever).</summary>
    Failed,
}

/// <summary>What one root's import did. <see cref="Problems"/> are the folders that need a human (ambiguous
/// or unmatched game folders); <see cref="Plan"/> carries the full detail for a results screen.</summary>
public sealed record ImportRunResult(BackupRoot Root, ImportRunStatus Status, ImportPlan? Plan, int Adopted,
                                     IReadOnlyList<string> Problems, Exception? Error = null)
{
    public bool NeedsHuman => Problems.Count > 0;
    /// <summary>(1284) The folder turned out to be a storage Grog already tracked (its files matched that root's
    /// records): <see cref="Root"/> is that root, re-pointed to the folder, not a new one.</summary>
    public bool MatchedExisting { get; init; }
}

/// <summary>
/// THE import sequence: register the folder as a root, and either defer (empty library: the flag lives on
/// the manifest so a restart before the first scan does not forget the promise) or plan against the library
/// and adopt the confident matches, stamped with the root's id. Both hosts had their own copy before 09-08
/// (the App's AddIntentExisting / ImportRootAsync / RunPendingImportsAsync, the CLI's `import`).
///
/// <para>Disk work throughout (folder walks, size stats): call from a worker.</para>
/// </summary>
public static class ImportRun
{
    /// <summary>Planner progress (folders done, total) for hosts that draw a bar; the host's Log gets the summary.</summary>
    public sealed class Options
    {
        public Action<int, int>? Progress { get; init; }
        /// <summary>How deep the planner walks before giving up on a folder (the CLI's --depth).</summary>
        public int MaxDepth { get; init; } = 2;
        /// <summary>(09-19) Verify what was adopted, straight after. On by default; tests of the adoption alone turn it off.</summary>
        public bool VerifyAdopted { get; init; } = true;
        /// <summary>Handed to the verify pass (see <see cref="VerifyRequest.FetchCurrentServerMd5"/>).</summary>
        public Func<GameFile, CancellationToken, Task<string?>>? FetchCurrentServerMd5 { get; init; }
        /// <summary>Also fetch and check GOG's checksum for adopted files with none on record (the CLI's
        /// --verify-checksums); needs <see cref="FetchCurrentServerMd5"/>.</summary>
        public bool FetchMissingChecksums { get; init; }
    }

    /// <summary>"Scan &amp; Add": register <paramref name="path"/> (idempotent; creates the folder) and import
    /// it now, or mark it pending when the library is empty. Saves either way.</summary>
    public static async Task<ImportRunResult> AddAndImportAsync(IManifestStore store, string path, ImportMode mode,
                                                                IBackupHost host, CancellationToken ct, Options? options = null)
    {
        // (1284) A folder whose files are a storage Grog already tracks IS that storage, back under another path
        // (a re-lettered stick, a renamed mount): re-point it instead of registering a second root beside an
        // empty first one. The backup folder carries no marker of Grog's; its content is the identity.
        if (MatchExistingRoot(store, path, options?.MaxDepth ?? 2) is { } matched)
        {
            bool samePath = !string.IsNullOrEmpty(matched.PathHint) && string.Equals(Path.GetFullPath(matched.PathHint), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
            if (!samePath) new VolumeService(store).RepointRoot(matched.Id, path);
            // Offline (the layout binds it next scan) like AddRoot's own re-add of a Detached folder: LastSeen is Bind's to stamp.
            store.Mutate(_ => { if (matched.State is RootState.Detached or RootState.Lost) matched.State = RootState.Offline; });
            await store.SaveAsync(ct);
            if (!samePath) host.Log($"{matched.Label} is at {Path.GetFullPath(path)} now: its files matched, nothing re-downloads.");
            var r = await ImportExistingRootAsync(store, matched, path, mode, host, ct, options);
            return r with { MatchedExisting = !samePath };
        }
        // AddRoot takes the gate itself for the Roots.Add; the folder create and path probe before it are disk.
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

    /// <summary>The storage already tracked whose records the folder's files belong to, or null. Plans the folder
    /// against the library (read-only), then counts, per root that is not currently bound at another live path,
    /// the matched files whose record already holds bytes on that root at the same relative path. The root with
    /// the most such files wins when they are at least half of what the folder matched and the root's own hint
    /// is not alive elsewhere (the same files present on two live drives are two storages). Disk work: worker only.</summary>
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
        // The same storage cannot be at two live paths: a root whose own folder still answers is not this one.
        if (!string.IsNullOrEmpty(root.PathHint) && !string.Equals(Path.GetFullPath(root.PathHint), full, StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(root.PathHint)) return null;
        return root;
    }

    /// <summary>The imports deferred at add time, now that a scan has stored items: for each runnable root
    /// whose folder answers, clear the flag, plan, adopt, save -- per root, so a crash mid-way forgets only
    /// what already ran. A root that does not answer keeps its flag for the next scan.</summary>
    public static async Task<IReadOnlyList<ImportRunResult>> RunPendingAsync(IManifestStore store, IBackupHost host,
                                                                             CancellationToken ct, Options? options = null)
    {
        var results = new List<ImportRunResult>();
        var due = store.Read(m => PendingImports.Runnable(m));
        foreach (var root in due)
        {
            ct.ThrowIfCancellationRequested();
            // A sleeping USB or network root can take seconds to answer: this is why the whole run is a worker call.
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

    /// <summary>The import half: plan <paramref name="path"/> against the library relative to
    /// <paramref name="root"/>, adopt the confident matches unless previewing, save. Never deletes or moves.
    /// Exceptions are caught into a <see cref="ImportRunStatus.Failed"/> result (cancellation is not).</summary>
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

            // Adopt the confident matches at once; problems stay for the host's Find-match. ApplyAsync gates
            // its own graph change and saves.
            if (plan.MatchedFileCount > 0) await planner.ApplyAsync(plan, mode, ct);
            host.Log($"Adopted {plan.AdoptedFiles} file(s) into {root.Label}.");
            // (09-19, owner) Adoption matches by name and size; that is not "the bytes are good". Every adopted
            // file is verified at once, the way a download is: MD5 where GOG's checksum is known, size otherwise.
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
        // By GAME, not by file: VerifyRun's file scope is a size-only confirm by contract. Unverified mode reads
        // only checksummed files that are not already Verified, so a game's other files cost a stat each.
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
