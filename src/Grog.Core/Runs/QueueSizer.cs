// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grog.Core.Manifest;
using Grog.Core.Models;

namespace Grog.Core.Runs;

public sealed class QueueSizer
{
    private readonly IManifestStore _store;
    private readonly Func<string, CancellationToken, Task<long?>> _realSize;
    private readonly Func<(long GogId, string FileKey), bool> _inFlight;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private Task? _run;
    private CancellationTokenSource? _cts;
    private bool _again;

    public event Action<int>? SizesLearned;

    /// <param name="realSize">The file's real byte count from the CDN for a manualUrl (FileKey), or null when GOG
    /// does not say; throws are counted as "unknown" for this pass and the file is left for the next.</param>
    /// <param name="inFlight">True when the engine is transferring this file right now: it learns its own size.</param>
    public QueueSizer(IManifestStore store, Func<string, CancellationToken, Task<long?>> realSize,
                      Func<(long GogId, string FileKey), bool> inFlight, Action<string>? log = null)
    {
        _store = store; _realSize = realSize; _inFlight = inFlight; _log = log ?? (_ => { });
    }

    public int BatchSize { get; init; } = 8;
    public TimeSpan Pace { get; init; } = TimeSpan.FromMilliseconds(250);
    public bool IsRunning { get { lock (_gate) return _run is { IsCompleted: false }; } }

    public static List<(long GogId, string FileKey)> Unsized(LibraryManifest m)
    {
        var list = new List<(long, string)>();
        foreach (var q in m.Downloads.Snapshot())
        {
            var f = m.ItemById(q.GogId)?.Files.FirstOrDefault(x => x.FileKey == q.FileKey);
            if (f is null || f.WireSizeBytes is not null || f.State == FileState.Unavailable) continue;
            list.Add((q.GogId, q.FileKey));
        }
        return list;
    }

    public void Request()
    {
        lock (_gate)
        {
            if (_run is { IsCompleted: false }) { _again = true; return; }
            _cts = new CancellationTokenSource();
            _run = Task.Run(() => RunAsync(_cts.Token));
        }
    }

    public void Stop()
    {
        lock (_gate) { _cts?.Cancel(); }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            int learned = 0, skipped = 0;
            do
            {
                lock (_gate) _again = false;
                var work = _store.Read(Unsized);
                if (work.Count == 0) break;
                _log($"Sizing {Format.Plural.Of(work.Count, "queued file")} against GOG's servers in the background…");
                int batch = 0, failures = 0;
                foreach (var key in work)
                {
                    ct.ThrowIfCancellationRequested();
                    if (_inFlight(key)) { skipped++; continue; }
                    long? size = null;
                    try { size = await _realSize(key.FileKey, ct).ConfigureAwait(false); failures = 0; }
                    catch (OperationCanceledException) { throw; }
                    catch
                    {
                        if (++failures >= 3) { _log("Sizing paused: GOG's servers are not answering. It resumes the next time something is queued."); return; }
                    }
                    if (size is { } real && real > 0)
                    {
                        bool wrote = false;
                        using (_store.Gate.Enter())
                        {
                            var f = _store.Current.ItemById(key.GogId)?.Files.FirstOrDefault(x => x.FileKey == key.FileKey);
                            if (f is not null && f.WireSizeBytes is null) { f.WireSizeBytes = real; wrote = true; }
                        }
                        if (wrote) { learned++; batch++; }
                    }
                    if (batch >= BatchSize) { SizesLearned?.Invoke(batch); batch = 0; }
                    if (Pace > TimeSpan.Zero) await Task.Delay(Pace, ct).ConfigureAwait(false);
                }
                if (batch > 0) SizesLearned?.Invoke(batch);
            }
            while (Again());
            if (learned > 0 || skipped > 0)
                _log($"Sized {Format.Plural.Of(learned, "queued file")}{(skipped > 0 ? $" ({skipped} left to their own transfer)" : "")}.");
        }
        catch (OperationCanceledException) { /* stopped: what landed stays */ }
        catch (Exception ex) { _log($"Sizing stopped: {ex.Message}"); }
    }

    private bool Again() { lock (_gate) return _again; }
}
