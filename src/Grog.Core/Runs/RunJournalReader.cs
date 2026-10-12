// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Grog.Core.Scheduling;

namespace Grog.Core.Runs;

public readonly record struct RunOutcome(string RunId, ScheduleRunOutcome? State, int Failed, string? Reason, bool ConnectionLost = false, string FailedKeys = "")
{
    public IEnumerable<(long GogId, string FileKey)> FailedFiles()
    {
        foreach (var line in FailedKeys.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            int bar = line.IndexOf('|');
            if (bar > 0 && long.TryParse(line.AsSpan(0, bar), out var id)) yield return (id, line[(bar + 1)..]);
        }
    }
}

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

public static class RunJournalReader
{
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
            if (ParseState(s.State ?? "") is null && !readUnfinished) return null;
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

    public static IEnumerable<string> ReadSharedLines(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        while (sr.ReadLine() is { } line) yield return line;
    }

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

    public static ScheduleRunOutcome? ParseState(string state) => state switch
    {
        "completed-with-failures" => ScheduleRunOutcome.Partial,
        "cancelled" => ScheduleRunOutcome.Canceled,   
        "running" or ""           => null,
        _ => Enum.TryParse<ScheduleRunOutcome>(state, ignoreCase: true, out var o) ? o : null,
    };
}
