// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace Grog.Core.Storage;

/// <summary>
/// Cross-process presence for the two Grog hosts sharing one profile (09-08). The App and the CLI each
/// hold a lock file in the config dir while they could write the manifest; the other side checks before
/// it writes. The App has priority: a CLI writer verb refuses to run while the App holds its lock, and
/// says so in grog.log (for anything parsing that log) and in a hand-off file the App turns into a toast.
/// A running CLI does not stop the App from opening; the App warns and lets the verb finish.
///
/// The LOCK is the truth (an exclusive open held for the process lifetime); the file's content (pid, verb,
/// time) is only for the message. A crashed holder leaves a file that nobody can lock, which reads as
/// "not running".
/// </summary>
public sealed class ProcessPresence : IDisposable
{
    public const string AppLockName = "grog-app.lock";
    public const string CliLockName = "grog-cli.lock";
    /// <summary>Written by a blocked CLI verb, consumed (deleted) by the App when it shows the toast.</summary>
    public const string BlockedNoteName = "cli-blocked.txt";

    private FileStream? _stream;
    public string Path { get; }
    public bool Held => _stream is not null;

    private ProcessPresence(string path) => Path = path;

    /// <summary>Take the named lock in <paramref name="configDir"/>; <see cref="Held"/> is false when another
    /// process has it. Never throws: a filesystem that refuses locks fails open (Held = true is not claimed,
    /// but nothing is blocked either).</summary>
    public static ProcessPresence TryHold(string configDir, string lockName, string note)
    {
        var p = new ProcessPresence(System.IO.Path.Combine(configDir, lockName));
        try
        {
            Directory.CreateDirectory(configDir);
            // FileShare.None: exclusive on Windows; an exclusive advisory lock on Unix. Held for our lifetime.
            var fs = new FileStream(p.Path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            p._stream = fs;
            // The message rides in an unlocked sidecar: on Unix a shared read of the locked file itself is
            // refused too (the lock is whole-file), so a prober could never read pid and note from it.
            File.WriteAllText(p.Path + ".info", $"{Environment.ProcessId}\n{note}\n{DateTimeOffset.Now:O}\n");
        }
        catch (IOException) { /* held by another process */ }
        catch (UnauthorizedAccessException) { /* Windows reports a held exclusive open this way too */ }
        catch { /* no lock support: fail open */ }
        return p;
    }

    /// <summary>Who holds the named lock, or null when nobody does. Content is best-effort (pid, note).</summary>
    public static (int Pid, string Note)? Holder(string configDir, string lockName)
    {
        var path = System.IO.Path.Combine(configDir, lockName);
        if (!File.Exists(path)) return null;
        try
        {
            // If WE can take it exclusively, nobody holds it (a leftover from a crash).
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

    /// <summary>Leave a note the App turns into a toast: "grogcli 'backup' was blocked while Grog was open".</summary>
    public static void WriteBlockedNote(string configDir, string verb)
    {
        try { File.WriteAllText(System.IO.Path.Combine(configDir, BlockedNoteName), $"{verb}\n{DateTimeOffset.Now:O}\n"); }
        catch { /* best-effort */ }
    }

    /// <summary>Read and remove the note, if any.</summary>
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
        // Only the holder unlinks: a refused instance must not delete the live holder's files (Unix unlinks a locked file).
        if (_stream is null) return;
        try { _stream.Dispose(); } catch { }
        _stream = null;
        try { File.Delete(Path + ".info"); } catch { }
        try { File.Delete(Path); } catch { /* another process may already hold a fresh one */ }
    }
}
