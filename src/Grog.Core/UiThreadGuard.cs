// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Concurrent;

namespace Grog.Core;

public static class UiThreadGuard
{
    public static Func<bool>? IsUiThread;
    public static Action<string>? Violation;
    public static bool Strict;

    private static readonly ConcurrentDictionary<string, byte> _reported = new();

    public static void NotOnUi(string what)
    {
        if (IsUiThread is not { } check || !check()) return;
        if (Strict) throw new InvalidOperationException($"UI thread ran {what}: the UI thread runs only UI.");
        if (_reported.TryAdd(what, 0)) try { Violation?.Invoke(what); } catch { /* reporting must never break the call */ }
    }
}
