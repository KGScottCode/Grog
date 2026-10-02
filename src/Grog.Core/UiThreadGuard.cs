// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Concurrent;

namespace Grog.Core;

/// <summary>
/// THE UI THREAD RUNS ONLY UI (owner, 09-25). Core's heavy entry points (whole-queue planning, library walks, drive
/// probes, settling, journal and manifest writes) call <see cref="NotOnUi"/>; a host with a UI thread registers
/// <see cref="IsUiThread"/>. A call from the UI thread is a defect: it is reported once per entry point through
/// <see cref="Violation"/> (the App logs it as an ERROR naming the call), and throws when <see cref="Strict"/> is on
/// (tests that drive UI-thread code paths). The work itself still runs, so a missed site is a log line, never a
/// broken feature. Cheap: one delegate call per entry.
/// </summary>
public static class UiThreadGuard
{
    /// <summary>Set by a host that has a UI thread; null (the CLI, most tests) means nothing is ever on it.</summary>
    public static Func<bool>? IsUiThread;
    /// <summary>Told once per entry point name when it runs on the UI thread.</summary>
    public static Action<string>? Violation;
    /// <summary>Throw instead of report (tests).</summary>
    public static bool Strict;

    private static readonly ConcurrentDictionary<string, byte> _reported = new();

    public static void NotOnUi(string what)
    {
        if (IsUiThread is not { } check || !check()) return;
        if (Strict) throw new InvalidOperationException($"UI thread ran {what}: the UI thread runs only UI.");
        if (_reported.TryAdd(what, 0)) try { Violation?.Invoke(what); } catch { /* reporting must never break the call */ }
    }
}
