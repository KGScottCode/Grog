// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System.Threading;
using System.Threading.Tasks;

namespace Grog.Core.Manifest;

/// <summary>
/// Persistence boundary for the manifest: the app works only against the in-memory
/// <see cref="LibraryManifest"/>; disk format is an implementation detail (JsonManifestStore is the default).
/// </summary>
public interface IManifestStore
{
    LibraryManifest Current { get; }
    /// <summary>The one door for changing <see cref="Current"/>; see <see cref="ManifestGate"/>.</summary>
    ManifestGate Gate { get; }
    Task LoadAsync(CancellationToken ct = default);
    Task SaveAsync(CancellationToken ct = default);
}
