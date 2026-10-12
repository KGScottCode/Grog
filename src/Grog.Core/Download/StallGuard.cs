// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Grog.Core.Download;

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
                // The timer cannot have run down yet
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

    internal void TripForTest() => _cts.Cancel();

    public void Dispose() => _cts.Dispose();
}
