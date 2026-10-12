// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Threading;

namespace Grog.Core.Manifest;

public sealed class ManifestGate
{
    private readonly object _lock = new();

    public Scope Enter()
    {
        if (Monitor.TryEnter(_lock)) return new Scope(_lock);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Monitor.Enter(_lock);
        try { SlowWait?.Invoke(sw.ElapsedMilliseconds); } catch { /* a diagnostic hook must never strand the gate */ }
        return new Scope(_lock);
    }

    public static System.Action<long>? SlowWait;

    public bool IsHeld => Monitor.IsEntered(_lock);

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

public static class ManifestStoreGateExtensions
{
    public static void Mutate(this IManifestStore store, Action<LibraryManifest> change)
    {
        using (store.Gate.Enter()) change(store.Current);
    }

    public static T Read<T>(this IManifestStore store, Func<LibraryManifest, T> read)
    {
        using (store.Gate.Enter()) return read(store.Current);
    }
}
