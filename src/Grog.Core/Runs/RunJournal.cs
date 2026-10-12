// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Grog.Core.Runs;

using Grog.Core.Scheduling;

public sealed class RunJournal : IDisposable
{
    private const int KeepRuns = 20;

    private static readonly JsonSerializerOptions Line = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,   // one event per line
    };
    private static readonly JsonSerializerOptions Pretty = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly object _lock = new();
    private readonly string _dir;
    private readonly string _eventsPath;
    private readonly string _currentPath;
    private readonly DateTimeOffset _startedAt;
    private readonly string _command;
    private int _completed, _failed, _total;
    private readonly HashSet<(long, string)> _failedKeys = new();
    private string _state = "running";
    private string? _currentFile;
    private bool _closed;
    private int _batchDepth;
    private System.Text.StringBuilder? _pending;
    private bool _currentDirty;

    public string EventsPath => _eventsPath;
    public string CurrentPath => _currentPath;
    public string RunId { get; }

    private RunJournal(string dir, string command, int total, DateTimeOffset startedAt, string runId)
    {
        _dir = dir;
        _command = command;
        _total = total;
        _startedAt = startedAt;
        RunId = runId;
        _eventsPath = Path.Combine(dir, $"run-{runId}-{Safe(command)}.jsonl");
        _currentPath = Path.Combine(dir, "current.json");
    }

    public static RunJournal? Start(string configDir, string command, int total, string? note = null)
    {
        try
        {
            var dir = Path.Combine(configDir, "runs");
            Directory.CreateDirectory(dir);
            var now = DateTimeOffset.Now;
            var j = new RunJournal(dir, command, total, now, now.ToString("yyyyMMdd-HHmmss"));
            j.Prune(dir);
            j.Append(new { ts = now, @event = "run.started", schema = JournalSchema, command, total, note });
            j.WriteCurrent();
            return j;
        }
        catch { return null; }
    }

    public void FileNotCounted(string name, string kind, long bytes, string? state = null, string? reason = null)
    {
        lock (_lock)
        {
            if (_closed) return;
            _currentFile = name;
            Append(new
            {
                ts = DateTimeOffset.Now,
                @event = "file." + kind,
                file = name,
                bytes,
                state,
                error = reason,
                completed = _completed,
                failed = _failed,
                total = _total,
            });
            WriteCurrent();
        }
    }

    public void File(string name, bool ok, long bytes, string? state = null, string? error = null, string? kind = null,
                     long gogId = 0, string? fileKey = null)
    {
        lock (_lock)
        {
            if (_closed) return;
            var key = (gogId, fileKey ?? "");
            if (ok) { _completed++; if (gogId != 0 && _failedKeys.Remove(key)) _failed--; }
            else if (gogId == 0 || _failedKeys.Add(key)) _failed++;
            _currentFile = name;
            Append(new
            {
                ts = DateTimeOffset.Now,
                @event = ok ? "file.completed" : "file.failed",
                file = name,
                gogId,
                fileKey,
                bytes,
                state,
                error,
                kind,
                completed = _completed,
                failed = _failed,
                total = _total,
            });
            WriteCurrent();
        }
    }

    public void Finish(ScheduleRunOutcome outcome, string? error = null)
    {
        lock (_lock)
        {
            if (_closed) return;
            if (_batchDepth > 0) { _batchDepth = 0; if (_pending is { Length: > 0 } text) { _pending = null; WriteLines(text.ToString()); } _currentDirty = false; }
            _state = outcome.ToString().ToLowerInvariant();
            Append(new
            {
                ts = DateTimeOffset.Now,
                @event = "run.finished",
                outcome = _state,
                error,
                completed = _completed,
                failed = _failed,
                total = _total,
                elapsedSeconds = (int)(DateTimeOffset.Now - _startedAt).TotalSeconds,
            });
            WriteCurrent();
            CloseEvents();
            _closed = true;
        }
    }

    public void BeginBatch() { lock (_lock) _batchDepth++; }

    public void EndBatch()
    {
        lock (_lock)
        {
            if (_batchDepth == 0 || --_batchDepth > 0) return;
            if (_pending is { Length: > 0 } text) { _pending = null; WriteLines(text.ToString()); }
            if (_currentDirty) { _currentDirty = false; WriteCurrentNow(); }
        }
    }

    private void Append(object evt)
    {
        var line = JsonSerializer.Serialize(evt, Line) + Environment.NewLine;
        if (_batchDepth > 0) { (_pending ??= new System.Text.StringBuilder()).Append(line); return; }
        WriteLines(line);
    }

    private FileStream? _events;
    private StreamWriter? _eventsWriter;
    private DateTime _lastFsyncUtc = DateTime.MinValue;
    internal TimeSpan FsyncEvery { get; set; } = TimeSpan.FromSeconds(2);

    private void WriteLines(string text)
    {
        UiThreadGuard.NotOnUi("a run-journal write");
        try
        {
            if (_eventsWriter is null)
            {
                _events = new FileStream(_eventsPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                _eventsWriter = new StreamWriter(_events);
            }
            _eventsWriter.Write(text);
            _eventsWriter.Flush();
            var now = DateTime.UtcNow;
            if (now - _lastFsyncUtc >= FsyncEvery) { _events!.Flush(flushToDisk: true); _lastFsyncUtc = now; }
        }
        catch { /* journalling must never break the run */ }
    }

    private void CloseEvents()
    {
        try
        {
            if (_eventsWriter is { } w) { w.Flush(); _events!.Flush(flushToDisk: true); w.Dispose(); }
        }
        catch { /* best-effort */ }
        finally { _eventsWriter = null; _events = null; }
    }

    public const int JournalSchema = 1;

    private void WriteCurrent()
    {
        if (_batchDepth > 0) { _currentDirty = true; return; }
        WriteCurrentNow();
    }

    private void WriteCurrentNow()
    {
        try
        {
            var doc = JsonSerializer.Serialize(new
            {
                schema = JournalSchema,  
                runId = RunId,
                command = _command,
                state = _state,
                startedAt = _startedAt,
                updatedAt = DateTimeOffset.Now,
                total = _total,
                completed = _completed,
                failed = _failed,
                currentFile = _currentFile,
                eventsFile = Path.GetFileName(_eventsPath),
            }, Pretty);

            var tmp = _currentPath + ".tmp";
            System.IO.File.WriteAllText(tmp, doc);
            System.IO.File.Move(tmp, _currentPath, overwrite: true);
        }
        catch { /* best-effort */ }
    }

    private static string Safe(string command)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in command) sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_');
        return sb.Length == 0 ? "run" : sb.ToString();
    }

    private void Prune(string dir)
    {
        try
        {
            var mine = Safe(_command);
            var files = new List<string>();
            foreach (var f in Directory.GetFiles(dir, "run-*.jsonl"))
            {
                var stem = Path.GetFileNameWithoutExtension(f);   // run-<stamp>[-<command>]
                var parts = stem.Split('-');
                var cmd = parts.Length >= 4 ? string.Join("-", parts, 3, parts.Length - 3) : mine;
                if (cmd == mine) files.Add(f);
            }
            files.Sort(StringComparer.Ordinal);   
            for (int i = 0; i < files.Count - (KeepRuns - 1); i++)
                try { System.IO.File.Delete(files[i]); } catch { /* in use / locked */ }
        }
        catch { /* best-effort */ }
    }

    public void Dispose() => Finish(ScheduleRunOutcome.Interrupted);
}
