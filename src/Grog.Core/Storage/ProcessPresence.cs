// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace Grog.Core.Storage;

public sealed class ProcessPresence : IDisposable
{
    public const string AppLockName = "grog-app.lock";
    public const string CliLockName = "grog-cli.lock";
    public const string BlockedNoteName = "cli-blocked.txt";

    private FileStream? _stream;
    public string Path { get; }
    public bool Held => _stream is not null;

    private ProcessPresence(string path) => Path = path;

    public static ProcessPresence TryHold(string configDir, string lockName, string note)
    {
        var p = new ProcessPresence(System.IO.Path.Combine(configDir, lockName));
        try
        {
            Directory.CreateDirectory(configDir);
            var fs = new FileStream(p.Path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            p._stream = fs;
            File.WriteAllText(p.Path + ".info", $"{Environment.ProcessId}\n{note}\n{DateTimeOffset.Now:O}\n");
        }
        catch (IOException) { /* held by another process */ }
        catch (UnauthorizedAccessException) { /* Windows reports a held exclusive open this way too */ }
        catch { /* no lock support: fail open */ }
        return p;
    }

    public static (int Pid, string Note)? Holder(string configDir, string lockName)
    {
        var path = System.IO.Path.Combine(configDir, lockName);
        if (!File.Exists(path)) return null;
        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return null;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch { return null; }
        try
        {
            var lines = File.ReadAllLines(path + ".info");
            int.TryParse(lines.Length > 0 ? lines[0] : "", out var pid);
            return (pid, lines.Length > 1 ? lines[1] : "");
        }
        catch { return (0, ""); }
    }

    public static void WriteBlockedNote(string configDir, string verb)
    {
        try { File.WriteAllText(System.IO.Path.Combine(configDir, BlockedNoteName), $"{verb}\n{DateTimeOffset.Now:O}\n"); }
        catch { /* best-effort */ }
    }

    public static string? TakeBlockedNote(string configDir)
    {
        var path = System.IO.Path.Combine(configDir, BlockedNoteName);
        try
        {
            if (!File.Exists(path)) return null;
            var verb = File.ReadLines(path).FirstOrDefault() ?? "";
            File.Delete(path);
            return verb;
        }
        catch { return null; }
    }

    public void Dispose()
    {
        if (_stream is null) return;
        try { _stream.Dispose(); } catch { }
        _stream = null;
        try { File.Delete(Path + ".info"); } catch { }
        try { File.Delete(Path); } catch { /* another process may already hold a fresh one */ }
    }
}
