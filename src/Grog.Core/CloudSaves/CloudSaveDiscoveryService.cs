// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grog.Core.Manifest;
using Grog.Core.Models;

namespace Grog.Core.CloudSaves;


/// <summary>Account-wide cloud-save discovery: one v2 containers call reconciled onto the manifest.
/// The GUI and CLI both call this so their notion of "games with cloud saves" can't drift.</summary>
public sealed class CloudSaveDiscoveryService
{
    private readonly CloudSaveService _cloud;

    public CloudSaveDiscoveryService(CloudSaveService cloud) => _cloud = cloud;

    /// <summary>Fetch containers, reconcile flags onto <paramref name="manifest"/> (the caller saves),
    /// and return every cloud-save row sorted by title. <paramref name="titleFor"/> resolves titles for
    /// products that have cloud saves but aren't in the local library.</summary>
    public async Task<IReadOnlyList<CloudSaveRow>> DiscoverAsync(
        LibraryManifest manifest,
        Func<long, CancellationToken, Task<string?>> titleFor,
        CancellationToken ct = default,
        ManifestGate? gate = null)
    {
        var containers = await _cloud.ListContainersAsync(ct);
        // The reconcile writes CloudByAccount + the legacy Cloud* fields: under the store's gate when the
        // caller passes one (the fetch above stays outside it). (manifest gate 09-08)
        List<CloudSaveRow> rows; List<long> unmatched;
        if (gate is not null)
        {
            using (gate.Enter()) (rows, unmatched) = CloudSaveReconciler.Apply(manifest, containers);
        }
        else (rows, unmatched) = CloudSaveReconciler.Apply(manifest, containers);


        foreach (var pid in unmatched)
        {
            var title = await titleFor(pid, ct) ?? $"Game {pid}";
            var c = containers.First(x => x.ProductId == pid);
            rows.Add(new CloudSaveRow(pid, title, "", c.SizeBytes, null, 0, 0));
        }
        return rows.OrderBy(r => r.Title).ToList();
    }
}
