// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System.Threading;
using System.Threading.Tasks;

namespace Grog.Core.Manifest;

public interface IManifestStore
{
    LibraryManifest Current { get; }
    ManifestGate Gate { get; }
    Task LoadAsync(CancellationToken ct = default);
    Task SaveAsync(CancellationToken ct = default);
}
