// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Threading;
using System.Threading.Tasks;
using Grog.Core.Manifest;

namespace Grog.App.Runtime;

public sealed class ManifestSaves
{
    public static ManifestSaves Default { get; } = new();

    public Action<string>? LogError { get; set; }
    public Action<string?>? NotSavedChanged { get; set; }
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
