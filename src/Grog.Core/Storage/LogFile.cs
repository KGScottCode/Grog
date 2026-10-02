// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace Grog.Core.Storage;

/// <summary>
/// Rolling text log beside the manifest (<c>%APPDATA%/Grog/grog.log</c>) so unattended-run failures
/// survive a restart. Dependency-free and best-effort: never throws into a backup, never grows without
/// bound. One line per entry, newest at the bottom, rotated at <see cref="MaxBytes"/> through <see cref="Generations"/> generations.
/// </summary>
public sealed class LogFile
{
    /// <summary>Rotate at 10 MB (~100k entries per generation) so months of unattended runs stay explainable.</summary>
    public const long MaxBytes = 10L * 1024 * 1024;

    /// <summary>Rotated files kept (grog.log.1 ... .3); enough that a mid-incident rotation never erases evidence.</summary>
    public const int Generations = 3;

    public string Path { get; }
    /// <summary>The most recent rotated generation.</summary>
    public string PreviousPath => GenerationPath(1);

    /// <summary>Path of rotated generation <paramref name="n"/> (1 = most recent).</summary>
    public string GenerationPath(int n) => Path + "." + n;

    private readonly object _gate = new();

    public LogFile(string configDir)
        => Path = System.IO.Path.Combine(configDir, "grog.log");

    // ---- Background writer (review 09-06) ----
    // The App logged from the UI thread with a synchronous append under a lock shared with download workers:
    // every worker line was posted to the dispatcher and written to disk THERE, on a USB stick in portable
    // mode. Post() hands the line to one writer thread; Append() stays synchronous for the crash guard and
    // for callers that must see the bytes on disk before continuing (tests).
    private BlockingCollection<(DateTimeOffset When, string Category, bool IsError, string Message)>? _pending;
    private Thread? _writer;

    /// <summary>Queue one entry for the writer thread; returns at once. Order is preserved.</summary>
    public void Post(DateTimeOffset when, string category, bool isError, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        var q = _pending;
        if (q is null)
        {
            lock (_gate)
            {
                if (_pending is null)
                {
                    _pending = new BlockingCollection<(DateTimeOffset, string, bool, string)>();
                    _writer = new Thread(WriterLoop) { IsBackground = true, Name = "grog-log" };
                    _writer.Start();
                }
                q = _pending;
            }
        }
        try { q.Add((when, category, isError, message)); Interlocked.Increment(ref _posted); } catch { /* completed: shutting down */ }
    }

    /// <summary>Write everything queued so far; call at shutdown so the last lines survive the process.
    /// Waits on lines POSTED, not lines queued: the writer takes a line before it is on disk.</summary>
    public void Flush(TimeSpan? timeout = null)
    {
        if (_pending is null) return;
        long target = Interlocked.Read(ref _posted);
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(3));
        while (Interlocked.Read(ref _written) < target && DateTime.UtcNow < deadline) Thread.Sleep(10);
    }
    private long _posted, _written;

    private void WriterLoop()
    {
        var q = _pending!;
        foreach (var (when, category, isError, message) in q.GetConsumingEnumerable())
        {
            Append(when, category, isError, message);
            Interlocked.Increment(ref _written);
        }
    }

    // ONE open stream per full path for the whole process, shared by every LogFile instance on it: the App
    // holds three instances on grog.log, and FileMode.Append only seeks to the end at open, so two streams on
    // one file overwrote each other's lines. The stream stays open (AutoFlush: each line reaches the OS at once)
    // and every write seeks to the end first, so a line another process (the CLI) appended in between is kept.
    // Shared for read, write and delete so that other process can append and a reader can open the file.
    private sealed class SharedStream
    {
        public readonly object Gate = new();
        public StreamWriter? Out;
        public int Users;
    }
    private static readonly Dictionary<string, SharedStream> Registry = new(StringComparer.Ordinal);
    private SharedStream? _shared;
    /// <summary>Test seam: the live file's rotation point.</summary>
    internal long RotateAt { get; set; } = MaxBytes;

    /// <summary>Append one entry. Never throws -- a logging failure must not take down whatever it was
    /// logging about.</summary>
    public void Append(DateTimeOffset when, string category, bool isError, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        // Collapse newlines: one entry is one line, so the file stays greppable and a multi-line exception
        // message can't masquerade as several entries.
        var flat = message.Replace("\r", " ").Replace("\n", " ").Trim();
        var line = $"{when:yyyy-MM-dd HH:mm:ss} {(isError ? "ERROR" : "INFO ")} [{category}] {flat}" + Environment.NewLine;
        try
        {
            var s = Attach();
            lock (s.Gate)
            {
                if (s.Out is null) Open(s);
                else if (s.Out.BaseStream.Length >= RotateAt) { CloseWriter(s); Open(s); }   // Open rotates the oversized file first
                s.Out!.BaseStream.Seek(0, SeekOrigin.End);   // past whatever another process appended since the last line
                s.Out.Write(line);
            }
        }
        catch { /* best-effort by design */ }
    }

    /// <summary>Joins this instance to the process-wide stream for its path.</summary>
    private SharedStream Attach()
    {
        lock (Registry)
        {
            if (_shared is null)
            {
                var key = System.IO.Path.GetFullPath(Path);
                if (!Registry.TryGetValue(key, out var s)) Registry[key] = s = new SharedStream();
                s.Users++;
                _shared = s;
            }
            return _shared;
        }
    }

    /// <summary>Detaches this instance; the stream itself closes when the last instance on the path detaches.
    /// The next <see cref="Append"/> reattaches. Call at shutdown after <see cref="Flush"/>.</summary>
    public void Close()
    {
        lock (Registry)
        {
            var s = _shared;
            if (s is null) return;
            _shared = null;
            if (--s.Users > 0) return;
            lock (s.Gate) CloseWriter(s);
            Registry.Remove(System.IO.Path.GetFullPath(Path));
        }
    }

    private void Open(SharedStream s)
    {
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        RotateIfNeeded();
        var fs = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        s.Out = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };
    }

    private static void CloseWriter(SharedStream s)
    {
        try { s.Out?.Dispose(); } catch { /* best-effort */ }
        s.Out = null;
    }

    /// <summary>Once the live file passes the cap, shift every generation down one (.2 becomes .3, and the
    /// oldest falls off) and move the live file to .1. Bounded at <see cref="Generations"/> files, so worst
    /// case on disk is (Generations + 1) * MaxBytes.</summary>
    private void RotateIfNeeded()
    {
        try
        {
            var fi = new FileInfo(Path);
            if (!fi.Exists || fi.Length < RotateAt) return;

            // Walk from the oldest down so each move lands on a slot we just vacated.
            var oldest = GenerationPath(Generations);
            if (File.Exists(oldest)) File.Delete(oldest);
            for (int n = Generations - 1; n >= 1; n--)
            {
                var from = GenerationPath(n);
                if (File.Exists(from)) File.Move(from, GenerationPath(n + 1));
            }
            File.Move(Path, GenerationPath(1));
        }
        catch { /* if rotation fails, keep appending -- a big log beats no log */ }
    }
}
