// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Grog.Core.Download;

/// <summary>Reads a body stream with a stall timer around each read: no bytes for <see cref="Timeout"/> is a
/// <see cref="StallException"/>. One linked source per file, re-armed before each read and disarmed after it.
/// CancelAfter(Infinite) does not stop a timer callback already in flight, so a source that trips after the
/// disarm is replaced before the next read, and a trip that arrives before the timeout could have elapsed is
/// treated as that stale callback, never as a stall.</summary>
internal sealed class StallGuard : IDisposable
{
    private readonly CancellationToken _outer;
    private readonly Stopwatch _armed = new();
    private CancellationTokenSource _cts;

    public TimeSpan Timeout { get; }

    public StallGuard(CancellationToken outer, TimeSpan timeout)
    {
        _outer = outer;
        Timeout = timeout;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
    }

    /// <summary>One read under the stall timer. Throws StallException on a real stall; rethrows the outer cancel.</summary>
    public async ValueTask<int> ReadAsync(Stream stream, Memory<byte> buffer)
    {
        while (true)
        {
            _outer.ThrowIfCancellationRequested();
            if (_cts.IsCancellationRequested) Renew();   // tripped by a callback in flight after the last disarm
            _cts.CancelAfter(Timeout);
            _armed.Restart();
            try
            {
                var read = await stream.ReadAsync(buffer, _cts.Token).ConfigureAwait(false);
                _cts.CancelAfter(System.Threading.Timeout.InfiniteTimeSpan);   // disarmed while the bytes are written and throttled
                if (_cts.IsCancellationRequested && !_outer.IsCancellationRequested) Renew();
                return read;
            }
            catch (OperationCanceledException) when (!_outer.IsCancellationRequested)
            {
                // The timer cannot have run down yet: a stale callback from the previous arm, not a stall. Fresh source, read again.
                if (_armed.Elapsed < Timeout - TimeSpan.FromMilliseconds(50)) { Renew(); continue; }
                throw new StallException(Timeout);
            }
        }
    }

    private void Renew()
    {
        _cts.Dispose();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(_outer);
    }

    /// <summary>Test seam: what a stall-timer callback that was already running at the disarm does.</summary>
    internal void TripForTest() => _cts.Cancel();

    public void Dispose() => _cts.Dispose();
}
