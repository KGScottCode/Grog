// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Threading;
using System.Threading.Tasks;
using Grog.Core.Manifest;

namespace Grog.App.Runtime;

/// <summary>
/// The one way the App fires a manifest save it does not await. A save that fails is logged once per distinct
/// reason and raised as a persistent "library not saved" state until a later save lands; a bare `_ = SaveAsync()`
/// dropped the exception and left the user with a library that silently stopped persisting.
/// </summary>
public sealed class ManifestSaves
{
    /// <summary>The App's instance; tests build their own so in-flight saves never cross classes.</summary>
    public static ManifestSaves Default { get; } = new();

    /// <summary>Error log sink (wired by the shell). Called on a pool thread.</summary>
    public Action<string>? LogError { get; set; }
    /// <summary>Raised with the reason when a save fails, and with null when a later save lands. Pool thread.</summary>
    public Action<string?>? NotSavedChanged { get; set; }
    /// <summary>The reason the last save failed, or null while saves land.</summary>
    public string? LastError => Volatile.Read(ref _lastError);

    private string? _lastError;
    private string? _lastLogged;
    private readonly object _lock = new();

    public void SaveInBackground(JsonManifestStore? store, string what)
    {
        if (store is null) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await store.SaveAsync().ConfigureAwait(false);
                if (Volatile.Read(ref _lastError) is null) return;
                lock (_lock) { _lastError = null; _lastLogged = null; }
                NotSavedChanged?.Invoke(null);
            }
            catch (Exception ex)
            {
                var reason = ex.Message;
                bool log;
                lock (_lock)
                {
                    _lastError = reason;
                    log = reason != _lastLogged;
                    if (log) _lastLogged = reason;
                }
                if (log) LogError?.Invoke($"Your library could not be saved ({what}): {ex}");
                NotSavedChanged?.Invoke(reason);
            }
        });
    }
}
