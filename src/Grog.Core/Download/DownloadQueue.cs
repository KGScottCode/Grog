// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System.Collections.Generic;
using System.Linq;

namespace Grog.Core.Download;

public sealed class QueuedDownload
{
    public long GogId { get; set; }
    public string FileKey { get; set; } = "";
    public string? TargetRootId { get; set; }
    public bool Fits { get; set; } = true;
}

public sealed class DownloadQueue
{
    [System.Text.Json.Serialization.JsonIgnore] private readonly object _gate = new();

    private List<QueuedDownload> _items = new();
    [System.Text.Json.Serialization.JsonIgnore] private Dictionary<(long GogId, string FileKey), QueuedDownload>? _index;

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

    // Callers hold _gate
    private Dictionary<(long GogId, string FileKey), QueuedDownload> Index()
    {
        if (_index is not null) return _index;
        var ix = new Dictionary<(long, string), QueuedDownload>(_items.Count);
        foreach (var q in _items) ix.TryAdd((q.GogId, q.FileKey), q);
        return _index = ix;
    }

    private QueuedDownload? Find(long gogId, string fileKey)
        => Index().TryGetValue((gogId, fileKey), out var q) ? q : null;

    public bool Paused { get; set; }

    public bool IsEmpty { get { lock (_gate) return _items.Count == 0; } }

    public List<QueuedDownload> Snapshot() { lock (_gate) return new List<QueuedDownload>(_items); }

    public List<QueuedDownload> WontFit() { lock (_gate) return _items.Where(q => !q.Fits).ToList(); }
    public bool AnyWontFit { get { lock (_gate) return _items.Any(q => !q.Fits); } }
    public int FittingCount { get { lock (_gate) return _items.Count(q => q.Fits); } }

    public bool Contains(long gogId, string fileKey) { lock (_gate) return Find(gogId, fileKey) is not null; }

    public bool Enqueue(long gogId, string fileKey, string? targetRootId = null) => Enqueue(gogId, fileKey, targetRootId, fits: true);

    public bool Enqueue(long gogId, string fileKey, string? targetRootId, bool fits)
    {
        lock (_gate)
        {
            if (Find(gogId, fileKey) is { } have)
            {
                // Already queued
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

    public List<QueuedDownload> RemoveWontFit()
    {
        lock (_gate)
        {
            var gone = _items.Where(q => !q.Fits).ToList();
            if (gone.Count > 0) { _items = _items.Where(q => q.Fits).ToList(); _index = null; }
            return gone;
        }
    }

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

    public void MoveTo(long gogId, string fileKey, int newIndex)
    {
        lock (_gate)
        {
            // Reorder only
            if (Find(gogId, fileKey) is not { } item) return;
            _items.Remove(item);
            newIndex = System.Math.Clamp(newIndex, 0, _items.Count);
            _items.Insert(newIndex, item);
        }
    }

    public void SetOrder(IReadOnlyList<(long GogId, string FileKey)> order)
    {
        lock (_gate)
        {
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
