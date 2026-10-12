// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace Grog.Core.Storage;

public sealed class LogFile
{
    public const long MaxBytes = 10L * 1024 * 1024;

    public const int Generations = 3;

    public string Path { get; }
    public string PreviousPath => GenerationPath(1);

    public string GenerationPath(int n) => Path + "." + n;

    private readonly object _gate = new();

    public LogFile(string configDir)
        => Path = System.IO.Path.Combine(configDir, "grog.log");

    // ---- Background writer ----
    private BlockingCollection<(DateTimeOffset When, string Category, bool IsError, string Message)>? _pending;
    private Thread? _writer;

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

    private sealed class SharedStream
    {
        public readonly object Gate = new();
        public StreamWriter? Out;
        public int Users;
    }
    private static readonly Dictionary<string, SharedStream> Registry = new(StringComparer.Ordinal);
    private SharedStream? _shared;
    internal long RotateAt { get; set; } = MaxBytes;

    public void Append(DateTimeOffset when, string category, bool isError, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        var flat = Grog.Core.Format.UrlRedaction.Scrub(message.Replace("\r", " ").Replace("\n", " ").Trim());
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

    private void RotateIfNeeded()
    {
        try
        {
            var fi = new FileInfo(Path);
            if (!fi.Exists || fi.Length < RotateAt) return;

            // Walk from the oldest down
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
