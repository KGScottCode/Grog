// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;

namespace Grog.Core.Volumes;

public static class ReorgQueueOps
{
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
