// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;

namespace Grog.Core.Sync;

public readonly record struct ProgressSnapshot(long DoneBytes, long TotalBytes)
{
    public double Percent => TotalBytes > 0 ? 100.0 * DoneBytes / TotalBytes : 0;
    public long RemainingBytes => Math.Max(0, TotalBytes - DoneBytes);
    public bool IsComplete => TotalBytes > 0 && DoneBytes >= TotalBytes;
}
