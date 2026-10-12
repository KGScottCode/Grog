// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.IO;

namespace Grog.Cli;

internal static class Out
{
    public static bool Silent { get; set; }

    private static string? _logPath;
    private static readonly object _logLock = new();

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
                File.AppendAllText(_logPath, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {level} {Grog.Core.Format.UrlRedaction.Scrub(text)}{Environment.NewLine}");
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

    public static void Info(string text = "") { if (!Silent) Console.Out.WriteLine(text); }

    public static void Dim(string text) { if (!Silent) Write(text, ConsoleColor.DarkGray, stderr: false); }

    public static void Heading(string text) { if (!Silent) Write(text, ConsoleColor.DarkGray, stderr: false); }

    public static void Success(string text) { if (!Silent) Write(text, ConsoleColor.Green, stderr: false); Log('I', text); }

    public static void Warn(string text) { if (!Silent) Write(text, ConsoleColor.Yellow, stderr: false); Log('W', text); }

    public static void Error(string text) { Write(text, ConsoleColor.Red, stderr: true); Log('E', text); }

    public static bool HadUsageError { get; private set; }


    public static void Usage(string text) { Write(text, ConsoleColor.Yellow, stderr: true); Log('U', text); HadUsageError = true; }

    public static bool Enabled => !Console.IsOutputRedirected && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));

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
