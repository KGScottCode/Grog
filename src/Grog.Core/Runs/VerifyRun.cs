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

public enum VerifyScope
{
    Library,
    Items,
    Files,
}

public sealed record VerifyRequest(
    VerifyMode Mode = VerifyMode.SizeOnly,
    IReadOnlyCollection<long>? ItemIds = null,
    IReadOnlyCollection<(long GogId, string FileKey)>? Files = null,
    bool Full = false)
{
    public Action<VerifyProgress>? Progress { get; init; }

    public Func<GameFile, CancellationToken, Task<string?>>? FetchCurrentServerMd5 { get; init; }

    public bool FetchMissingChecksums { get; init; }

    public VerifyScope Scope => Files is not null ? VerifyScope.Files : ItemIds is not null ? VerifyScope.Items : VerifyScope.Library;
    public VerifyMode EffectiveMode => Full ? VerifyMode.FullRehash : Mode;

    // ---- the shapes the hosts ask for ----
    public static VerifyRequest LibrarySizeOnly() => new(VerifyMode.SizeOnly);
    public static VerifyRequest LibraryRescan(bool full = false) => new(VerifyMode.Unverified, Full: full);
    public static VerifyRequest Cli(bool full) => new(full ? VerifyMode.FullRehash : VerifyMode.SizeOnly);
    public static VerifyRequest ForItems(IEnumerable<long> ids, VerifyMode mode = VerifyMode.Unverified) => new(mode, ItemIds: ids.ToList());
    public static VerifyRequest ConfirmItems(IEnumerable<long> ids) => new(VerifyMode.SizeOnly, ItemIds: ids.ToList());
    public static VerifyRequest ForFiles(IEnumerable<(long GogId, string FileKey)> keys, VerifyMode mode = VerifyMode.SizeOnly)
        => new(mode, Files: keys.ToList());
}

public sealed record VerifyRunResult(VerifyResult Verify, VerifyScope Scope, VerifyMode Mode, int Items, int Files)
{
    public bool AnyProblems => Verify.Corrupt > 0 || Verify.Missing > 0;
    public int Ok => Verify.Verified + Verify.SizeOnlyOk;
}

public static class VerifyRun
{
    public static Task<VerifyRunResult> RunAsync(IManifestStore store, string backupRoot, VerifyRequest request,
                                                 IBackupHost host, CancellationToken ct)
        => RunAsync(store, new BackupLayout(store.Current, backupRoot), backupRoot, request, host, ct);

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
                r = await vs.RunAsync(mode, ct);
                break;
            }
        }

        var result = new VerifyRunResult(r, request.Scope, mode, items, files);
        host.Log(Describe(result), isError: result.AnyProblems);
        return result;
    }

    public static string Describe(VerifyRunResult v)
    {
        var r = v.Verify;
        var offline = r.OfflineSkipped > 0 ? $" ({r.OfflineSkipped} on offline storage, skipped)" : "";
        var updated = r.Updated > 0 ? $", {r.Updated} updated" : "";
        return $"Verify: {v.Ok} ok, {r.Corrupt} corrupt, {r.Missing} missing{updated}{offline}.";
    }
}
