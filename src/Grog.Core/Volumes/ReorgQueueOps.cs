// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;

namespace Grog.Core.Volumes;

/// <summary>Reprioritizing the not-yet-started moves of a running (or paused) reorg. Only Pending moves
/// move; settled and in-flight moves keep their slots. Thread-safe against the runner via
/// <see cref="ReorgJob.SyncRoot"/> -- the runner always picks the first non-settled move, so putting a
/// Pending move earlier makes it run sooner.</summary>
public static class ReorgQueueOps
{
    /// <summary>Reposition <paramref name="item"/> to <paramref name="newPendingIndex"/> within the
    /// Pending subset (0-based). No-op if the item isn't Pending (already running or done).</summary>
    public static void MovePendingTo(ReorgJob job, ReorgMove item, int newPendingIndex)
    {
        lock (job.SyncRoot)
        {
            var slots = new List<int>();
            for (int i = 0; i < job.Moves.Count; i++)
                if (job.Moves[i].State == ReorgMoveState.Pending) slots.Add(i);

            var pending = slots.Select(i => job.Moves[i]).ToList();
            int cur = pending.IndexOf(item);
            if (cur < 0) return;   // not a pending move

            newPendingIndex = Math.Clamp(newPendingIndex, 0, pending.Count - 1);
            if (newPendingIndex == cur) return;

            pending.RemoveAt(cur);
            pending.Insert(newPendingIndex, item);
            for (int k = 0; k < slots.Count; k++) job.Moves[slots[k]] = pending[k];
        }
    }

    /// <summary>Re-sequence the Pending subset by <paramref name="key"/> (stable), keeping settled and in-flight
    /// moves in their slots -- the Moving pane's "Order by" pills, the twin of the download queue's sort.</summary>
    /// <returns>True when the order changed.</returns>
    public static bool SortPending<TKey>(ReorgJob job, Func<ReorgMove, TKey> key, bool ascending)
    {
        lock (job.SyncRoot)
        {
            var slots = new List<int>();
            for (int i = 0; i < job.Moves.Count; i++)
                if (job.Moves[i].State == ReorgMoveState.Pending) slots.Add(i);
            if (slots.Count < 2) return false;
            var pending = slots.Select(i => job.Moves[i]).ToList();
            var ordered = (ascending ? pending.OrderBy(key) : pending.OrderByDescending(key)).ToList();
            if (ordered.SequenceEqual(pending)) return false;
            for (int k = 0; k < slots.Count; k++) job.Moves[slots[k]] = ordered[k];
            return true;
        }
    }
}
