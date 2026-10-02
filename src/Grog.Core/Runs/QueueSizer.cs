// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grog.Core.Manifest;
using Grog.Core.Models;

namespace Grog.Core.Runs;

/// <summary>
/// Learns the REAL size of queued files before they download. GOG lists a display size only (a 325 MB label for a
/// 341,311,456-byte file; errors up to 45 MB were measured on one library), and the planner prices every unstarted
/// file at that label, so on a nearly full drive each landing moved the fit line and "files left" would not step
/// down with "done" (walk 10-01). The CDN's Content-Length is the only real figure GOG exposes, two requests per
/// file (resolve the link, HEAD it), so the walk is bounded to what matters: files that are QUEUED and not yet
/// sized, in queue order, one at a time under the API client's own rate gate, started when something is queued
/// (never at launch, never after a plain scan). The answer is persisted on the record (<see cref="GameFile.WireSizeBytes"/>,
/// kept across launches, cleared by a sync only when the file's version changes), so each file is asked once.
/// </summary>
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

    /// <summary>Raised off the UI thread after a batch of sizes landed (the host re-plans and saves).</summary>
    public event Action<int>? SizesLearned;

    /// <param name="realSize">The file's real byte count from the CDN for a manualUrl (FileKey), or null when GOG
    /// does not say; throws are counted as "unknown" for this pass and the file is left for the next.</param>
    /// <param name="inFlight">True when the engine is transferring this file right now: it learns its own size.</param>
    public QueueSizer(IManifestStore store, Func<string, CancellationToken, Task<long?>> realSize,
                      Func<(long GogId, string FileKey), bool> inFlight, Action<string>? log = null)
    {
        _store = store; _realSize = realSize; _inFlight = inFlight; _log = log ?? (_ => { });
    }

    /// <summary>How many sizes one pass learns before it tells the host (a re-plan and a save per batch, not per file).</summary>
    public int BatchSize { get; init; } = 8;
    /// <summary>Pause between files on top of the API client's own gate: the sizer is background work.</summary>
    public TimeSpan Pace { get; init; } = TimeSpan.FromMilliseconds(250);
    public bool IsRunning { get { lock (_gate) return _run is { IsCompleted: false }; } }

    /// <summary>The queued files still priced at GOG's label (the work left), in queue order.</summary>
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

    /// <summary>Something was queued: size what is unsized. A running pass picks the new rows up on its next loop.</summary>
    public void Request()
    {
        lock (_gate)
        {
            if (_run is { IsCompleted: false }) { _again = true; return; }
            _cts = new CancellationTokenSource();
            _run = Task.Run(() => RunAsync(_cts.Token));
        }
    }

    /// <summary>Stop (sign-out, Fresh Library, quit). Sizes already learned stay.</summary>
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
                        // This file keeps its label for now; the engine learns the size when it downloads. Three in a
                        // row is the connection, not the files: stop the pass, the next queueing asks again.
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
