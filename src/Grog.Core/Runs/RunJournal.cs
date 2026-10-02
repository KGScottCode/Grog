// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Grog.Core.Runs;

using Grog.Core.Scheduling;

/// <summary>
/// Crash-survivable record of one long run, written to disk as it happens. Two files: runs/run-&lt;stamp&gt;.jsonl
/// (append-only, one event per line -- a crash can only damage the last line) and runs/current.json (live
/// summary, replaced atomically via temp+rename). Every method is best-effort and swallows IO errors: a journal
/// must never be the reason a backup fails.
/// </summary>
public sealed class RunJournal : IDisposable
{
    /// <summary>How many finished run files to keep; older runs are pruned on start.</summary>
    private const int KeepRuns = 20;

    private static readonly JsonSerializerOptions Line = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,   // one event per line; indenting would break the append-only contract
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
    // (QA 09-30 C5) "failed" is a count of FILES, not of strikes: a file that fails twice in one run (re-added by Try
    // Again, or retried after a drive wait) is one failed file, and one that fails and then lands is none.
    private readonly HashSet<(long, string)> _failedKeys = new();
    private string _state = "running";
    private string? _currentFile;
    private bool _closed;
    // (09-25) A settle pass writes many lines at once: batched, one append + one fsync + one current.json per pass
    // instead of two file writes and an fsync per file (a queue-sized cancel paid that on the calling thread).
    private int _batchDepth;
    private System.Text.StringBuilder? _pending;
    private bool _currentDirty;

    /// <summary>Absolute path of this run's event log, surfaced so the CLI can tell the user where to look.</summary>
    public string EventsPath => _eventsPath;
    /// <summary>Absolute path of the live-summary file; stable across runs so a watcher polls one path.</summary>
    public string CurrentPath => _currentPath;
    public string RunId { get; }

    private RunJournal(string dir, string command, int total, DateTimeOffset startedAt, string runId)
    {
        _dir = dir;
        _command = command;
        _total = total;
        _startedAt = startedAt;
        RunId = runId;
        // The command rides in the name so pruning keeps a history PER command: twenty per-file clicks
        // in the App must not rotate the nightly backup runs out of the folder.
        _eventsPath = Path.Combine(dir, $"run-{runId}-{Safe(command)}.jsonl");
        _currentPath = Path.Combine(dir, "current.json");
    }

    /// <summary>Opens a journal under &lt;configDir&gt;/runs and writes the opening event. Returns null if the
    /// folder can't be created; callers treat null as "journalling off" and carry on.</summary>
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

    /// <summary>Records a file that ended without landing and without FAILING: stopped by the user, refused by
    /// GOG (Unavailable), or skipped because no owner is signed in. Written for the record, never counted in
    /// <c>failed</c>: Health reads that number as "files that failed in the last run", and none of these did.</summary>
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

    /// <summary>Records one finished file. <paramref name="ok"/> false means it FAILED (an error or a condemned
    /// file); a stop, a refusal or a skip goes through <see cref="FileNotCounted"/>.</summary>
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

    /// <summary>Closes the run with an outcome in the schedule vocabulary. A journal without this line is how
    /// a reader detects a hard crash: the run started, files landed, it never finished.</summary>
    public void Finish(ScheduleRunOutcome outcome, string? error = null)
    {
        lock (_lock)
        {
            if (_closed) return;
            // A batch still open here is flushed first, so the closing line is always the last one on disk.
            if (_batchDepth > 0) { _batchDepth = 0; if (_pending is { Length: > 0 } text) { _pending = null; WriteLines(text.ToString()); } _currentDirty = false; }
            // Lowercase on disk is the journal's file format; ParseState reads it back case-insensitively.
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

    /// <summary>Open a batch: lines and the summary are buffered until the matching <see cref="EndBatch"/>.</summary>
    public void BeginBatch() { lock (_lock) _batchDepth++; }

    /// <summary>Close a batch: every buffered line in one append with one fsync, then current.json once.</summary>
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

    // One stream for the run: opening, appending and fsyncing per batch cost a file open and a disk sync per settle
    // pass. The writer flushes each batch to the OS at once (a reader polling the file sees it); the disk sync
    // runs at most every FsyncEvery and on Finish, so a hard kill loses at most that window.
    private FileStream? _events;
    private StreamWriter? _eventsWriter;
    private DateTime _lastFsyncUtc = DateTime.MinValue;
    /// <summary>Test seam: the longest a written line waits for its disk sync.</summary>
    internal TimeSpan FsyncEvery { get; set; } = TimeSpan.FromSeconds(2);

    private void WriteLines(string text)
    {
        UiThreadGuard.NotOnUi("a run-journal write");
        try
        {
            if (_eventsWriter is null)
            {
                // Shared for read, write and delete: a reader polls it, a torn-tail test appends to it, a prune may remove it.
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

    /// <summary>The closing sync: every line reaches the disk and the stream is released.</summary>
    private void CloseEvents()
    {
        try
        {
            if (_eventsWriter is { } w) { w.Flush(); _events!.Flush(flushToDisk: true); w.Dispose(); }
        }
        catch { /* best-effort */ }
        finally { _eventsWriter = null; _events = null; }
    }

    /// <summary>Version of current.json and the events file (09-08). Documented in docs/CLI.md; a
    /// parser can refuse or adapt on a value it does not know. 1 = the shapes shipped in v0.1.0.</summary>
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
                schema = JournalSchema,   // (09-08) the automation contract's version: bump on any shape change
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

            // Temp + rename: a polling reader sees either the old file or the new one, never a torn one.
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

    /// <summary>Keeps the newest <see cref="KeepRuns"/> journals per command. Legacy stamp-only names count
    /// under this journal's command, so a pre-09-04 folder still thins out.</summary>
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
            files.Sort(StringComparer.Ordinal);   // stamped names sort chronologically
            for (int i = 0; i < files.Count - (KeepRuns - 1); i++)
                try { System.IO.File.Delete(files[i]); } catch { /* in use / locked */ }
        }
        catch { /* best-effort */ }
    }

    /// <summary>Closes an unfinished run as Interrupted; a hard kill leaves no finish line at all, by design.</summary>
    public void Dispose() => Finish(ScheduleRunOutcome.Interrupted);
}
