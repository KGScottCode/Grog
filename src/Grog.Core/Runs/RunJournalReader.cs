// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Grog.Core.Scheduling;

namespace Grog.Core.Runs;

/// <summary>A finished (or abandoned) run, as recorded on disk. A null State means the summary never saw a
/// finish line -- still "running" after a hard kill.</summary>
/// <param name="ConnectionLost">The representative (first) failure was the connection going away, as classified
/// where it was caught; the files keep their partials and Resume continues them.</param>
/// <param name="FailedKeys">The failed files as "gogId|fileKey" lines (schema 2+ journals; empty when the journal predates them),
/// so a reader can tell a failure that is already back in the queue from one that is not.</param>
public readonly record struct RunOutcome(string RunId, ScheduleRunOutcome? State, int Failed, string? Reason, bool ConnectionLost = false, string FailedKeys = "")
{
    /// <summary>The failed files as (gogId, fileKey) pairs.</summary>
    public IEnumerable<(long GogId, string FileKey)> FailedFiles()
    {
        foreach (var line in FailedKeys.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            int bar = line.IndexOf('|');
            if (bar > 0 && long.TryParse(line.AsSpan(0, bar), out var id)) yield return (id, line[(bar + 1)..]);
        }
    }
}

/// <summary>The live-summary file's shape, typed; <see cref="RunJournal"/> emits exactly these members.</summary>
public sealed class RunSummary
{
    [JsonPropertyName("schema")] public int Schema { get; set; }   // 0 = written before the field existed
    [JsonPropertyName("runId")] public string RunId { get; set; } = "";
    [JsonPropertyName("command")] public string Command { get; set; } = "";
    [JsonPropertyName("state")] public string State { get; set; } = "";
    [JsonPropertyName("startedAt")] public DateTimeOffset? StartedAt { get; set; }
    [JsonPropertyName("updatedAt")] public DateTimeOffset? UpdatedAt { get; set; }
    [JsonPropertyName("total")] public int Total { get; set; }
    [JsonPropertyName("completed")] public int Completed { get; set; }
    [JsonPropertyName("failed")] public int Failed { get; set; }
    [JsonPropertyName("currentFile")] public string? CurrentFile { get; set; }
    [JsonPropertyName("eventsFile")] public string? EventsFile { get; set; }
}

/// <summary>Reads what <see cref="RunJournal"/> wrote; kept separate from the writer's append/flush machinery.</summary>
public static class RunJournalReader
{
    /// <summary>Reads the last recorded run's outcome off disk; the journal is the only durable record that a
    /// transiently-failed run was ever tried. Returns null when there is no journal, it is unreadable, or the
    /// run finished cleanly -- callers only care about the bad news.</summary>
    public static RunOutcome? ReadLastFailure(string configDir) => ReadLastFailure(configDir, readUnfinished: false);

    /// <param name="readUnfinished">Also read a journal whose run never finished (a crash): startup's call.</param>
    public static RunOutcome? ReadLastFailure(string configDir, bool readUnfinished)
    {
        try
        {
            var path = Path.Combine(configDir, "runs", "current.json");
            if (!File.Exists(path)) return null;
            var s = JsonSerializer.Deserialize<RunSummary>(File.ReadAllText(path));
            if (s is null || s.Failed <= 0) return null;
            // A run still writing its journal is not the LAST run: its strikes read as "failed in the last backup run"
            // while it was live, and Dismiss then deleted the live journal (QA 09-30 C2). A run that died without
            // finishing stays "running" for good; its reader (startup) treats it like a finished one.
            if (ParseState(s.State ?? "") is null && !readUnfinished) return null;

            // The summary counts failures but never says why; take the first failure's message from the event
            // log as representative (a systemic cause repeats it on every line anyway).
            string? reason = null; bool network = false; var keys = new List<string>();
            if (s.EventsFile is not null)
            {
                var log = Path.Combine(configDir, "runs", s.EventsFile);
                if (File.Exists(log))
                {
                    foreach (var line in ReadSharedLines(log))
                    {
                        if (line.Length == 0) continue;
                        try
                        {
                            using var evt = JsonDocument.Parse(line);
                            var kind = evt.RootElement.TryGetProperty("event", out var k) ? k.GetString() : null;
                            string? keyLine = evt.RootElement.TryGetProperty("gogId", out var gid) && gid.ValueKind == JsonValueKind.Number && gid.GetInt64() > 0
                                && evt.RootElement.TryGetProperty("fileKey", out var fk) && fk.ValueKind == JsonValueKind.String
                                ? gid.GetInt64() + "|" + fk.GetString() : null;
                            // A file that failed and then landed in the same run is not a failed file (C5).
                            if (kind == "file.completed" && keyLine is not null) { keys.RemoveAll(x => x == keyLine); continue; }
                            if (kind == "file.failed")
                            {
                                if (keyLine is not null && !keys.Contains(keyLine)) keys.Add(keyLine);
                                if (reason is null && evt.RootElement.TryGetProperty("error", out var err))
                                {
                                    reason = err.GetString();
                                    network = evt.RootElement.TryGetProperty("kind", out var kd) && kd.ValueKind == JsonValueKind.String
                                              && kd.GetString() == "network";
                                    if (string.IsNullOrWhiteSpace(reason)) reason = null;
                                }
                            }
                        }
                        catch { /* torn last line -- skip it */ }
                    }
                }
            }
            return new RunOutcome(s.RunId, ParseState(s.State ?? ""), s.Failed, reason, network, keys.Count == 0 ? "" : string.Join('\n', keys) + "\n");
        }
        catch { return null; }
    }

    /// <summary>Lines of the live events file. File.ReadLines opens with FileShare.Read, which a Windows writer
    /// holding the stream denies; the journal opens its stream shared for read, write and delete, and so must a reader.</summary>
    public static IEnumerable<string> ReadSharedLines(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        while (sr.ReadLine() is { } line) yield return line;
    }

    /// <summary>The last run's summary, whatever its outcome: what a "last run" card shows across restarts.
    /// Null when there is no journal or it is unreadable.</summary>
    public static RunSummary? ReadLast(string configDir)
    {
        try
        {
            var path = Path.Combine(configDir, "runs", "current.json");
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<RunSummary>(File.ReadAllText(path));
        }
        catch { return null; }
    }

    /// <summary>Maps the on-disk state word to the shared vocabulary, including legacy lowercase spellings.</summary>
    public static ScheduleRunOutcome? ParseState(string state) => state switch
    {
        "completed-with-failures" => ScheduleRunOutcome.Partial,
        "cancelled" => ScheduleRunOutcome.Canceled,   // journals written before the 09-01 US-spelling rename
        "running" or ""           => null,
        _ => Enum.TryParse<ScheduleRunOutcome>(state, ignoreCase: true, out var o) ? o : null,
    };
}
