// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using Grog.Core.Storage;

namespace Grog.App.Services;

/// <summary>
/// Last line of defense for the three places an exception can otherwise vanish: the UI thread (Avalonia
/// would tear the process down), a faulted Task nobody awaited (the finalizer swallows it), and anything
/// else on the AppDomain (process exit with no trace). Every one is written to grog.log WITH its stack,
/// because a stranger's bug report is only as good as that line. UI-thread faults are marked handled so
/// a rendering or binding slip does not kill a backup mid-run; the process-level ones cannot be rescued,
/// so they are recorded and let through. Installed once, before the first window exists.
/// </summary>
public static class CrashGuard
{
    private static LogFile? _log;
    private static Action<string>? _notify;

    /// <param name="log">The app's grog.log sink.</param>
    /// <param name="notify">Optional UI-thread callback carrying a one-line message for a toast.</param>
    public static void Install(LogFile log, Action<string>? notify = null)
    {
        _log = log;
        _notify = notify;

        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Record("UI thread", e.Exception);
            e.Handled = true;   // keep the window (and any running backup) alive; the log has the truth
            try { _notify?.Invoke(Summary(e.Exception)); } catch { /* the toast is a courtesy */ }
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Record("background task", e.Exception);
            e.SetObserved();
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            // Terminating: nothing to rescue, but the WHY must reach disk before the process goes.
            Record(e.IsTerminating ? "FATAL" : "process", e.ExceptionObject as Exception);
            // (UI-thread sweep 09-06 r2) a debounced settings save (SaveSoon) that has not landed yet would
            // otherwise die with the process: write it now, after the crash line.
            if (e.IsTerminating) FlushSettings();
        };

        // (UI-thread sweep 09-06 r2) orderly exits too: the last SaveSoon may still be inside its ~500 ms window.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => FlushSettings();
    }

    private static void FlushSettings()
    {
        try { AppSettings.FlushPendingSave(); } catch { /* never throw from the exit path */ }
    }

    /// <summary>Set (or replace) the toast callback once the main view-model exists.</summary>
    public static void SetNotifier(Action<string>? notify) => _notify = notify;

    private static void Record(string where, Exception? ex)
    {
        try
        {
            var text = ex is null ? "(no exception object)" : ex.ToString();   // type, message, full stack
            _log?.Flush(TimeSpan.FromSeconds(1));   // queued lines first, so the crash reads in order and nothing before it is lost
            _log?.Append(DateTimeOffset.Now, "crash", true, $"unhandled exception on {where}: {text}");
        }
        catch { /* never throw from the crash path */ }
    }

    private static string Summary(Exception ex)
    {
        var inner = ex is AggregateException ag && ag.InnerException is { } ie ? ie : ex;
        return $"Something went wrong: {inner.GetType().Name}: {inner.Message} Details are in grog.log "
             + "(About > Open Log File) - the Copy button in Files & issues attaches it to a bug report.";
    }
}
