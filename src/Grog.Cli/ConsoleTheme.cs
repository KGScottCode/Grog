// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.IO;

namespace Grog.Cli;

/// <summary>
/// One place for all CLI coloring and output routing, so every command shares a consistent, restrained
/// palette AND a consistent contract for silent/scheduled runs.
///
/// Color carries meaning, default text carries content; color marks only status. Respects NO_COLOR and
/// output redirection (piping to a file or CI gets plain text automatically).
///
///   Success  green   -- a thing finished OK ("Sync complete")
///   Warn     yellow  -- in-progress or attention ("3 corrupt", "Syncing…")
///   Error    red     -- a failure (goes to STDERR)
///   Dim      gray    -- secondary metadata (paths, counts you glance past)
///   Info     default -- primary content (list rows, titles) -- no color
///
/// Silent mode (<c>--silent</c>, for scheduled/headless runs): suppresses stdout chatter and progress, but
/// failures ALWAYS surface -- Error still writes to stderr, and every Success/Warn/Error is appended to a
/// findable local log (<c>&lt;config&gt;/logs/grog.log</c>) so a run that failed at 3am leaves a trail in one
/// obvious, cross-platform place, no matter how output was redirected.
/// </summary>
internal static class Out
{
    /// <summary>Quiet stdout + progress (set from --silent). Failures still reach stderr and the log.</summary>
    public static bool Silent { get; set; }

    private static string? _logPath;
    private static readonly object _logLock = new();

    /// <summary>Point the run-log at the config dir (call once at startup). Best-effort; never throws.</summary>
    public static void ConfigureLog(string configDir)
    {
        try
        {
            var dir = Path.Combine(configDir, "logs");
            Directory.CreateDirectory(dir);
            _logPath = Path.Combine(dir, "grog.log");
        }
        catch { _logPath = null; }
    }


    private static void Log(char level, string text)
    {
        if (_logPath is null) return;
        try
        {
            lock (_logLock)
                File.AppendAllText(_logPath, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {level} {text}{Environment.NewLine}");
        }
        catch { /* logging is best-effort; never let it break a run */ }
    }

    private static void Write(string text, ConsoleColor color, bool stderr)
    {
        var w = stderr ? Console.Error : Console.Out;
        bool useColor = !(stderr ? Console.IsErrorRedirected : Console.IsOutputRedirected)
                        && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));
        if (useColor)
        {
            var prev = Console.ForegroundColor;
            Console.ForegroundColor = color;
            w.WriteLine(text);
            Console.ForegroundColor = prev;
        }
        else w.WriteLine(text);
    }

    /// <summary>Primary content -- no color. Suppressed in silent mode.</summary>
    public static void Info(string text = "") { if (!Silent) Console.Out.WriteLine(text); }

    /// <summary>Secondary metadata -- suppressed in silent mode, not logged (it's glance-past noise).</summary>
    public static void Dim(string text) { if (!Silent) Write(text, ConsoleColor.DarkGray, stderr: false); }

    /// <summary>A subtle heading -- suppressed in silent mode.</summary>
    public static void Heading(string text) { if (!Silent) Write(text, ConsoleColor.DarkGray, stderr: false); }

    /// <summary>A thing finished OK -- shown unless silent, always logged.</summary>
    public static void Success(string text) { if (!Silent) Write(text, ConsoleColor.Green, stderr: false); Log('I', text); }

    /// <summary>Attention/in-progress -- shown unless silent, always logged.</summary>
    public static void Warn(string text) { if (!Silent) Write(text, ConsoleColor.Yellow, stderr: false); Log('W', text); }

    /// <summary>A failure -- ALWAYS surfaces to stderr and the log, even in silent mode.</summary>
    public static void Error(string text) { Write(text, ConsoleColor.Red, stderr: true); Log('E', text); }

    /// <summary>Set once any usage/argument error has been reported, so the process can exit with code 2
    /// instead of masking a mistake as success.</summary>
    public static bool HadUsageError { get; private set; }


    /// <summary>Report a usage/argument error: always to stderr + the log, and flags the process to exit 2.
    /// Use for "Usage: …", missing required args, bad values -- anything the user must fix and re-run.</summary>
    public static void Usage(string text) { Write(text, ConsoleColor.Yellow, stderr: true); Log('U', text); HadUsageError = true; }

    /// <summary>Whether color is active on stdout (for callers that build colored segments themselves).</summary>
    public static bool Enabled => !Console.IsOutputRedirected && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));

    /// <summary>Write a colored segment inline (no newline) -- used by the progress renderer. Silent-gated.</summary>
    public static void Segment(string text, ConsoleColor color)
    {
        if (Silent) return;
        if (Enabled)
        {
            var prev = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.Write(text);
            Console.ForegroundColor = prev;
        }
        else Console.Write(text);
    }
}
