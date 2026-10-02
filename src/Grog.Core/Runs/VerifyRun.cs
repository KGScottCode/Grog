// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Verify;
using Grog.Core.Volumes;

namespace Grog.Core.Runs;

/// <summary>What a verify pass covers: everything with a local path, the named products, or the named files.</summary>
public enum VerifyScope
{
    /// <summary>Every manifest file with a recorded local path (Folders "Rescan &amp; Verify", startup check, CLI verify).</summary>
    Library,
    /// <summary>Only the products in <see cref="VerifyRequest.ItemIds"/> (per-game verify, Confirm downloaded).</summary>
    Items,
    /// <summary>Only the files in <see cref="VerifyRequest.Files"/>; their parents are the rollup set.</summary>
    Files,
}

/// <summary>
/// The inputs of one verify pass. <see cref="Mode"/> is how hard it reads (a stat, MD5 for unverified files,
/// MD5 for everything); <see cref="Full"/> is the "force full re-verify" switch and lifts whatever mode was
/// asked to <see cref="VerifyMode.FullRehash"/>. Scope is implied by which id list is set (see
/// <see cref="Scope"/>). A "confirm" is a size-only pass over items or files.
/// </summary>
public sealed record VerifyRequest(
    VerifyMode Mode = VerifyMode.SizeOnly,
    IReadOnlyCollection<long>? ItemIds = null,
    IReadOnlyCollection<(long GogId, string FileKey)>? Files = null,
    bool Full = false)
{
    /// <summary>Per-file progress (worker thread). Hosts marshal to their UI themselves.</summary>
    public Action<VerifyProgress>? Progress { get; init; }

    /// <summary>GOG's current checksum for a slot, so a hash mismatch on a repacked build reads Outdated rather
    /// than Corrupt. Null (offline, CLI) treats every mismatch as corruption -- the safe default.</summary>
    public Func<GameFile, CancellationToken, Task<string?>>? FetchCurrentServerMd5 { get; init; }

    /// <summary>(09-25) Also fetch and check GOG's checksum for files with none on record
    /// (<see cref="VerifyService.FetchMissingChecksums"/>). Needs a hashing mode and a fetcher.</summary>
    public bool FetchMissingChecksums { get; init; }

    public VerifyScope Scope => Files is not null ? VerifyScope.Files : ItemIds is not null ? VerifyScope.Items : VerifyScope.Library;
    public VerifyMode EffectiveMode => Full ? VerifyMode.FullRehash : Mode;

    // ---- the shapes the hosts ask for ----
    /// <summary>Whole library, size-only (startup existence check, the after-backup pass).</summary>
    public static VerifyRequest LibrarySizeOnly() => new(VerifyMode.SizeOnly);
    /// <summary>Whole library, read-frugal (Folders "Rescan &amp; Verify"); <paramref name="full"/> re-hashes everything.</summary>
    public static VerifyRequest LibraryRescan(bool full = false) => new(VerifyMode.Unverified, Full: full);
    /// <summary>The CLI's `verify [--full]`: fast size mode, or a full MD5 sweep.</summary>
    public static VerifyRequest Cli(bool full) => new(full ? VerifyMode.FullRehash : VerifyMode.SizeOnly);
    /// <summary>Per-game "Rescan &amp; Verify Files".</summary>
    public static VerifyRequest ForItems(IEnumerable<long> ids, VerifyMode mode = VerifyMode.Unverified) => new(mode, ItemIds: ids.ToList());
    /// <summary>"Confirm downloaded" on selected products: presence + size only.</summary>
    public static VerifyRequest ConfirmItems(IEnumerable<long> ids) => new(VerifyMode.SizeOnly, ItemIds: ids.ToList());
    /// <summary>Confirm / verify specific files.</summary>
    public static VerifyRequest ForFiles(IEnumerable<(long GogId, string FileKey)> keys, VerifyMode mode = VerifyMode.SizeOnly)
        => new(mode, Files: keys.ToList());
}

/// <summary>What the pass found, plus the scope it actually covered.</summary>
public sealed record VerifyRunResult(VerifyResult Verify, VerifyScope Scope, VerifyMode Mode, int Items, int Files)
{
    public bool AnyProblems => Verify.Corrupt > 0 || Verify.Missing > 0;
    public int Ok => Verify.Verified + Verify.SizeOnlyOk;
}

/// <summary>
/// THE verify sequence: bind the layout to the backup root, resolve the scope to files and their parent
/// products, run <see cref="VerifyService"/> in the requested mode, roll product statuses up and save.
/// The App had six copies of this (Activity, Folders, Health, Library x2, Scope) and the CLI a seventh
/// before 09-08; each built its own layout and picked its own VerifyService overload.
///
/// <para>Disk work: the <see cref="BackupLayout"/> constructor probes every root and the pass stats or
/// hashes files. Call from a worker (the App's RunBusy already is one).</para>
/// </summary>
public static class VerifyRun
{
    public static Task<VerifyRunResult> RunAsync(IManifestStore store, string backupRoot, VerifyRequest request,
                                                 IBackupHost host, CancellationToken ct)
        => RunAsync(store, new BackupLayout(store.Current, backupRoot), backupRoot, request, host, ct);

    /// <summary>Same, with a layout the caller already holds (BackupRun's after-pass, tests).</summary>
    public static async Task<VerifyRunResult> RunAsync(IManifestStore store, IBackupLayout layout, string backupRoot,
                                                       VerifyRequest request, IBackupHost host, CancellationToken ct)
    {
        var mode = request.EffectiveMode;
        var vs = new VerifyService(store, backupRoot) { Layout = layout, FetchCurrentServerMd5 = request.FetchCurrentServerMd5, FetchMissingChecksums = request.FetchMissingChecksums };
        if (request.Progress is { } progress) vs.Progress += progress;

        VerifyResult r;
        int items, files;
        switch (request.Scope)
        {
            case VerifyScope.Files:
            {
                // Resolve keys to entries under the gate (one consistent read); the pass itself runs outside it.
                var (fileList, parents) = store.Read(m =>
                {
                    var want = new HashSet<(long, string)>(request.Files!);
                    var fl = new List<GameFile>(); var ps = new List<LibraryItem>();
                    foreach (var i in m.Items)
                    {
                        bool any = false;
                        foreach (var f in i.Files)
                            if (want.Contains((i.GogId, f.FileKey))) { fl.Add(f); any = true; }
                        if (any) ps.Add(i);
                    }
                    return (fl, ps);
                });
                items = parents.Count; files = fileList.Count;
                // A file pass is a size-only CONFIRM (MD5 was checked at download time) unless the caller asked
                // for a full re-hash: the Storage file row's "Rescan & verify" reads the bytes (09-19). The
                // read-frugal Unverified mode stays a confirm here, as every other single-file site expects.
                if (mode != VerifyMode.FullRehash) mode = VerifyMode.SizeOnly;
                r = await vs.RunForFilesAsync(fileList, parents, mode, ct);
                break;
            }
            case VerifyScope.Items:
            {
                var itemList = store.Read(m =>
                {
                    var ids = new HashSet<long>(request.ItemIds!);
                    return m.Items.Where(i => ids.Contains(i.GogId)).ToList();
                });
                items = itemList.Count; files = itemList.Sum(i => i.Files.Count);
                r = mode == VerifyMode.SizeOnly
                    ? await vs.ConfirmAsync(itemList, ct)
                    : await vs.RunForItemsAsync(itemList, mode, ct);
                break;
            }
            default:
            {
                items = store.Read(m => m.Items.Count);
                files = store.Read(m => m.Items.Sum(i => i.Files.Count));
                // RunAsync rolls EVERY product up and saves (the post-pass); the scoped overloads above roll
                // up just what they touched and save too. Nothing is left for the host to persist.
                r = await vs.RunAsync(mode, ct);
                break;
            }
        }

        var result = new VerifyRunResult(r, request.Scope, mode, items, files);
        host.Log(Describe(result), isError: result.AnyProblems);
        return result;
    }

    /// <summary>One line for a log: "Verify: 12 ok, 0 corrupt, 1 missing (3 on offline storage, skipped)".</summary>
    public static string Describe(VerifyRunResult v)
    {
        var r = v.Verify;
        var offline = r.OfflineSkipped > 0 ? $" ({r.OfflineSkipped} on offline storage, skipped)" : "";
        var updated = r.Updated > 0 ? $", {r.Updated} updated" : "";
        return $"Verify: {v.Ok} ok, {r.Corrupt} corrupt, {r.Missing} missing{updated}{offline}.";
    }
}
