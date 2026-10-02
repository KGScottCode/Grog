// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System.Collections.Generic;
using System.Linq;

namespace Grog.Core.Download;

/// <summary>One file waiting to be (or being) downloaded. Identity is (GogId, FileKey); TargetRootId is
/// the device the placement planner picked, carried so a resume lands it in the same place.</summary>
public sealed class QueuedDownload
{
    public long GogId { get; set; }
    public string FileKey { get; set; } = "";
    public string? TargetRootId { get; set; }
    /// <summary>False when the last placement found no online device with room for this file. The file
    /// KEEPS ITS POSITION in the queue (inform, never choose -- owner, 09-11); the engine skips it and the
    /// Activity pane paints it WON'T FIT. Re-decided by every Place / Replan / ReplanWholeQueue. Defaults
    /// true so a queue persisted before this field existed loads as it did.</summary>
    public bool Fits { get; set; } = true;
}

/// <summary>
/// The download queue as a persisted fact: an ordered list plus one Paused flag, living in the manifest.
/// Single source of truth for "what is queued"; the engine executes it, every UI surface derives from it,
/// and only the explicit actions mutate it (enqueue, remove-on-complete, pause/resume, stop, reorder, cancel).
/// </summary>
public sealed class DownloadQueue
{
    // The queue is touched from both the UI thread (rendering, user actions) and the background download
    // task (enqueue at start, remove on complete). All access is guarded so an enumeration on one thread
    // can't crash on a mutation from the other ("Collection was modified"). Reads for rendering go through
    // Snapshot(), which returns a private copy.
    [System.Text.Json.Serialization.JsonIgnore] private readonly object _gate = new();

    private List<QueuedDownload> _items = new();
    // Key -> entry index over _items. The list stays the persisted truth; the index is rebuilt lazily after
    // a load and kept in step by every mutator, so Enqueue/SetPlacement/Contains/Remove stop scanning the list.
    [System.Text.Json.Serialization.JsonIgnore] private Dictionary<(long GogId, string FileKey), QueuedDownload>? _index;

    /// <summary>Files to download, in priority order (index 0 = next). The JSON face of the queue: the
    /// getter hands out a COPY taken under the gate, so the serializer (which runs while workers enqueue
    /// and complete) can never see a mid-mutation list; the setter is the loader's. Every mutation goes
    /// through the methods below, never through this list. A loaded list with duplicate keys keeps the FIRST
    /// entry of each and drops the rest, so the list and the index always agree on which entry a key names.</summary>
    public List<QueuedDownload> Items
    {
        get { lock (_gate) return new List<QueuedDownload>(_items); }
        set
        {
            lock (_gate)
            {
                var seen = new HashSet<(long, string)>();
                _items = (value ?? new List<QueuedDownload>()).Where(q => seen.Add((q.GogId, q.FileKey))).ToList();
                _index = null;
            }
        }
    }

    // Callers hold _gate. Duplicate keys in a loaded list resolve to the FIRST entry, matching FirstOrDefault.
    private Dictionary<(long GogId, string FileKey), QueuedDownload> Index()
    {
        if (_index is not null) return _index;
        var ix = new Dictionary<(long, string), QueuedDownload>(_items.Count);
        foreach (var q in _items) ix.TryAdd((q.GogId, q.FileKey), q);
        return _index = ix;
    }

    private QueuedDownload? Find(long gogId, string fileKey)
        => Index().TryGetValue((gogId, fileKey), out var q) ? q : null;

    /// <summary>The queue's own Paused fact. Paused files stay in the queue (partials kept); a game is
    /// "paused" iff it has a file here while this is true -- derived, never stored per-game.</summary>
    public bool Paused { get; set; }

    public bool IsEmpty { get { lock (_gate) return _items.Count == 0; } }

    /// <summary>A point-in-time copy of the queue, safe to enumerate on any thread.</summary>
    public List<QueuedDownload> Snapshot() { lock (_gate) return new List<QueuedDownload>(_items); }

    /// <summary>(09-22) The queued files marked won't-fit, in queue order.</summary>
    public List<QueuedDownload> WontFit() { lock (_gate) return _items.Where(q => !q.Fits).ToList(); }
    /// <summary>Any queued file marked won't-fit.</summary>
    public bool AnyWontFit { get { lock (_gate) return _items.Any(q => !q.Fits); } }
    /// <summary>How many queued files fit where they are routed: what a Resume would actually start.</summary>
    public int FittingCount { get { lock (_gate) return _items.Count(q => q.Fits); } }

    public bool Contains(long gogId, string fileKey) { lock (_gate) return Find(gogId, fileKey) is not null; }

    /// <summary>Append a file if it isn't already queued (idempotent). Returns true if it was added.</summary>
    public bool Enqueue(long gogId, string fileKey, string? targetRootId = null) => Enqueue(gogId, fileKey, targetRootId, fits: true);

    /// <summary>Append, or update the placement of an already-queued file: target AND whether it fits. A
    /// re-plan is the newest fact for both; the row never moves.</summary>
    public bool Enqueue(long gogId, string fileKey, string? targetRootId, bool fits)
    {
        lock (_gate)
        {
            if (Find(gogId, fileKey) is { } have)
            {
                // Already queued: the placement is the newest fact. Before 09-05 a re-plan (every Resume) left
                // the FIRST plan's target on the record while the engine used the new one.
                if (targetRootId is not null) have.TargetRootId = targetRootId;
                have.Fits = fits;
                return false;
            }
            var added = new QueuedDownload { GogId = gogId, FileKey = fileKey, TargetRootId = targetRootId, Fits = fits };
            _items.Add(added);
            Index()[(gogId, fileKey)] = added;
            return true;
        }
    }

    /// <summary>Update the placement (target + fits) of a file that IS queued. False, and nothing written, when
    /// it is not: a re-plan works from a snapshot, and a file that completed or was cancelled in the meantime
    /// must not come back as a phantom row through the appending <see cref="Enqueue(long,string,string?,bool)"/>.</summary>
    public bool SetPlacement(long gogId, string fileKey, string? targetRootId, bool fits)
    {
        lock (_gate)
        {
            if (Find(gogId, fileKey) is not { } have) return false;
            if (targetRootId is not null) have.TargetRootId = targetRootId;
            have.Fits = fits;
            return true;
        }
    }

    /// <summary>Remove every file currently marked won't-fit, leaving the rest in their order. The banner's
    /// "Remove them from the queue". Returns the removed entries (for the caller's confirmation text).</summary>
    public List<QueuedDownload> RemoveWontFit()
    {
        lock (_gate)
        {
            var gone = _items.Where(q => !q.Fits).ToList();
            if (gone.Count > 0) { _items = _items.Where(q => q.Fits).ToList(); _index = null; }
            return gone;
        }
    }

    /// <summary>Remove one file (a completed download, or a user cancel). Returns true if present.</summary>
    public bool Remove(long gogId, string fileKey)
    {
        lock (_gate)
        {
            if (Find(gogId, fileKey) is not { } have) return false;
            _items.Remove(have);
            Index().Remove((gogId, fileKey));
            return true;
        }
    }

    /// <summary>Move a queued file to a new 0-based position within the queue (reorder action). No-op if
    /// the file isn't queued.</summary>
    public void MoveTo(long gogId, string fileKey, int newIndex)
    {
        lock (_gate)
        {
            // Reorder only: the index maps keys to entries, not positions, so it needs no update here.
            if (Find(gogId, fileKey) is not { } item) return;
            _items.Remove(item);
            newIndex = System.Math.Clamp(newIndex, 0, _items.Count);
            _items.Insert(newIndex, item);
        }
    }

    /// <summary>Reorder the whole queue to match <paramref name="order"/> (a list of (GogId, FileKey) keys,
    /// most-important first). Items present in <paramref name="order"/> take that order; any queued item NOT
    /// named keeps its original relative order at the end. Used by the sortable queue (sort by name/size/...)
    /// and by scoped re-queue, which compute the desired order and apply it here.</summary>
    public void SetOrder(IReadOnlyList<(long GogId, string FileKey)> order)
    {
        lock (_gate)
        {
            // O(N) via a key->item map + a used-set, instead of O(N^2) FindIndex/RemoveAt in a loop
            // (matters for big queues -- this runs whenever the sortable queue re-sequences).
            var byKey = new Dictionary<(long, string), QueuedDownload>(_items.Count);
            foreach (var q in _items) byKey[(q.GogId, q.FileKey)] = q;
            var used = new HashSet<(long, string)>();
            var result = new List<QueuedDownload>(_items.Count);
            foreach (var (gogId, fileKey) in order)
                if (byKey.TryGetValue((gogId, fileKey), out var q) && used.Add((gogId, fileKey)))
                    result.Add(q);
            foreach (var q in _items)   // anything not named keeps its relative order, appended
                if (!used.Contains((q.GogId, q.FileKey))) result.Add(q);
            _items = result;
        }
    }

    /// <summary>Stop: empty the queue and clear paused. Partials on disk are untouched (nothing deleted).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _items.Clear();
            _index?.Clear();
            Paused = false;
        }
    }
}
