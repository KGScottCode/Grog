// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;

namespace Grog.Core.Sync;

/// <summary>A point-in-time view of a transfer at one level (file, product, or library): how much of
/// how many bytes, and the derived percent. Same shape at every level.</summary>
public readonly record struct ProgressSnapshot(long DoneBytes, long TotalBytes)
{
    public double Percent => TotalBytes > 0 ? 100.0 * DoneBytes / TotalBytes : 0;
    public long RemainingBytes => Math.Max(0, TotalBytes - DoneBytes);
    public bool IsComplete => TotalBytes > 0 && DoneBytes >= TotalBytes;
}
