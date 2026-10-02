// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Threading;

namespace Grog.Core.Manifest;

/// <summary>
/// The one door for changing the manifest graph. Download workers (settling), the sync service (reconcile),
/// the hosts (commands) and the store (serialising for a save) all held the same object graph with no
/// shared exclusion before 09-08: a save serialised a live graph with retries and, after eight misses,
/// dropped the write. Every mutation now runs inside <see cref="Enter"/>, and the store serialises inside
/// it too, so a save always sees a quiescent graph.
///
/// A monitor, not a semaphore: reentrant on the owning thread (a command that mutates and then calls a
/// helper that mutates must not deadlock) and never held across an await (the compiler will not let a
/// <c>using</c> scope span one in a way that changes threads; keep awaits outside the scope).
/// </summary>
public sealed class ManifestGate
{
    private readonly object _lock = new();

    /// <summary>Hold the gate for the scope's lifetime. Reentrant.</summary>
    public Scope Enter()
    {
        if (Monitor.TryEnter(_lock)) return new Scope(_lock);
        // Contended: time the wait (diagnostic only, walk 09-25: a 4.3 s UI stall on a queue re-sort with no timed pass).
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Monitor.Enter(_lock);
        try { SlowWait?.Invoke(sw.ElapsedMilliseconds); } catch { /* a diagnostic hook must never strand the gate */ }
        return new Scope(_lock);
    }

    /// <summary>Raised on the WAITING thread after a contended <see cref="Enter"/>, with the wait in ms. The App
    /// hooks it to the UI watchdog so a stall spent waiting for the gate is named as that. Decides nothing.</summary>
    public static System.Action<long>? SlowWait;

    /// <summary>True when the calling thread holds the gate; debug assertions in mutators use it.</summary>
    public bool IsHeld => Monitor.IsEntered(_lock);

    /// <summary>Canary for the handful of central mutators (sync apply, settle, add root/account, import
    /// apply): throws when the calling thread does not hold the gate. Cheap (one Monitor query), so it is
    /// always compiled in. (manifest gate 09-08)</summary>
    public void AssertHeld()
    {
        if (!IsHeld) throw new InvalidOperationException("manifest mutated outside the gate");
    }

    public readonly struct Scope : IDisposable
    {
        private readonly object _o;
        internal Scope(object o) => _o = o;
        public void Dispose() => Monitor.Exit(_o);
    }
}

/// <summary>Mutate/Read helpers over <see cref="IManifestStore.Gate"/>.</summary>
public static class ManifestStoreGateExtensions
{
    /// <summary>Run a synchronous change to the graph under the gate.</summary>
    public static void Mutate(this IManifestStore store, Action<LibraryManifest> change)
    {
        using (store.Gate.Enter()) change(store.Current);
    }

    /// <summary>Compute something from the graph under the gate (a consistent read across many fields).</summary>
    public static T Read<T>(this IManifestStore store, Func<LibraryManifest, T> read)
    {
        using (store.Gate.Enter()) return read(store.Current);
    }
}
