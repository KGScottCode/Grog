// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Grog.Core.Runs;
using Grog.Core.Tests.Framework;

/// <summary>
/// The journal exists to survive a hard kill, so these tests care about one thing above all: what is ON DISK
/// at each moment, not what the object returns. Every assertion re-reads the files.
/// </summary>
[NewBatch]
public class RunJournalTests
{
    private string _dir = "";

    [Setup]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "grog-journal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [Teardown]
    public void Teardown()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir; leave it if locked */ }
    }

    private string RunsDir => Path.Combine(_dir, "runs");

    private static List<JsonElement> Lines(string path) =>
        File.ReadAllLines(path).Where(l => l.Length > 0)
            .Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();

    private static JsonElement Doc(string path) =>
        JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();

    private static string EventOf(JsonElement e) => e.GetProperty("event").GetString() ?? "";

    [Test]
    public void Start_writes_the_opening_event_immediately()
    {
        var j = RunJournal.Start(_dir, "download", 3)!;
        Assert.NotNull(j);
        // Before ANY file completes, the log must already exist -- a run killed one second in still leaves proof.
        var lines = Lines(j.EventsPath);
        Assert.Equal(1, lines.Count);
        Assert.Equal("run.started", EventOf(lines[0]));
        Assert.Equal(3, lines[0].GetProperty("total").GetInt32());
    }

    [Test]
    public void Each_file_is_on_disk_before_the_run_ends()
    {
        var j = RunJournal.Start(_dir, "download", 2)!;
        j.File("setup_a.exe", ok: true, bytes: 100);

        // Read WITHOUT finishing: this is the crash case. The completed file must already be recorded.
        var lines = Lines(j.EventsPath);
        Assert.Equal(2, lines.Count);
        Assert.Equal("file.completed", EventOf(lines[1]));
        Assert.Equal("setup_a.exe", lines[1].GetProperty("file").GetString());
        Assert.Equal(1, lines[1].GetProperty("completed").GetInt32());
    }

    [Test]
    public void Failures_record_the_reason()
    {
        var j = RunJournal.Start(_dir, "download", 1)!;
        j.File("dlc.bin", ok: false, bytes: 12, state: "Failed", error: "disk full");
        var last = Lines(j.EventsPath)[^1];
        Assert.Equal("file.failed", EventOf(last));
        Assert.Equal("disk full", last.GetProperty("error").GetString());
        Assert.Equal(1, last.GetProperty("failed").GetInt32());
    }

    [Test]
    public void An_unfinished_run_is_detectable_as_a_crash()
    {
        var j = RunJournal.Start(_dir, "download", 5)!;
        j.File("a", ok: true, bytes: 1);
        // Simulate the kill: never call Finish/Dispose.
        Assert.False(Lines(j.EventsPath).Any(e => EventOf(e) == "run.finished"), "no run.finished after a kill");

        var current = Doc(j.CurrentPath);
        Assert.Equal("running", current.GetProperty("state").GetString());
        Assert.Equal(1, current.GetProperty("completed").GetInt32());
    }

    [Test]
    public void Finish_closes_the_run_with_its_outcome()
    {
        var j = RunJournal.Start(_dir, "download", 1)!;
        j.File("a", ok: true, bytes: 1);
        j.Finish(Grog.Core.Scheduling.ScheduleRunOutcome.Completed);

        var last = Lines(j.EventsPath)[^1];
        Assert.Equal("run.finished", EventOf(last));
        Assert.Equal("completed", last.GetProperty("outcome").GetString());
        Assert.Equal("completed", Doc(j.CurrentPath).GetProperty("state").GetString());
    }

    [Test]
    public void Finish_flushes_every_line_written_through_the_open_stream()
    {
        // The stream stays open for the run and syncs to disk on a window; Finish syncs the rest and closes it,
        // so a reader after Finish sees every line, batched or not, and the closing line last.
        var j = RunJournal.Start(_dir, "download", 3)!;
        j.FsyncEvery = TimeSpan.FromHours(1);   // no window sync inside this test: Finish is what lands the lines
        j.File("a", ok: true, bytes: 1);
        j.BeginBatch();
        j.File("b", ok: true, bytes: 2);
        j.File("c", ok: false, bytes: 3, error: "boom");
        j.EndBatch();
        j.Finish(Grog.Core.Scheduling.ScheduleRunOutcome.Failed);

        var lines = Lines(j.EventsPath);
        Assert.Equal(5, lines.Count, "start, three files, finish");
        Assert.Equal("file.completed", EventOf(lines[1]));
        Assert.Equal("file.completed", EventOf(lines[2]));
        Assert.Equal("file.failed", EventOf(lines[3]));
        Assert.Equal("run.finished", EventOf(lines[4]));
        Assert.Equal(2, Doc(j.CurrentPath).GetProperty("completed").GetInt32());
        // Closed: the file can be moved, which a stream still open would refuse on Windows.
        File.Move(j.EventsPath, j.EventsPath + ".moved");
        Assert.True(File.Exists(j.EventsPath + ".moved"), "the stream was released by Finish");
    }

    [Test]
    public void Finish_is_idempotent_so_dispose_after_finish_does_not_reopen_it()
    {
        var j = RunJournal.Start(_dir, "download", 1)!;
        j.Finish(Grog.Core.Scheduling.ScheduleRunOutcome.Completed);
        j.Dispose();   // would otherwise append a second, contradictory "interrupted" line
        Assert.Equal(1, Lines(j.EventsPath).Count(e => EventOf(e) == "run.finished"));
    }

    [Test]
    public void A_torn_last_line_does_not_destroy_the_lines_before_it()
    {
        var j = RunJournal.Start(_dir, "download", 2)!;
        j.File("a", ok: true, bytes: 1);
        // Simulate a process killed MID-WRITE: a truncated fragment, exactly what a torn append leaves behind.
        File.AppendAllText(j.EventsPath, "{\"ts\":\"2026-08-10T00:00:00\",\"event\":\"file.comp");

        var good = new List<JsonElement>();
        foreach (var line in File.ReadAllLines(j.EventsPath))
        {
            try { good.Add(JsonDocument.Parse(line).RootElement.Clone()); } catch { /* torn tail -- discard */ }
        }
        // The whole point of append-only: damage is confined to the final line.
        Assert.Equal(2, good.Count);
        Assert.Equal("file.completed", EventOf(good[1]));
    }

    [Test]
    public void Current_json_is_replaced_not_appended()
    {
        var j = RunJournal.Start(_dir, "download", 3)!;
        j.File("a", ok: true, bytes: 1);
        j.File("b", ok: true, bytes: 1);
        // It must always parse as ONE document -- an append would make it invalid JSON after the first update.
        var current = Doc(j.CurrentPath);
        Assert.Equal(2, current.GetProperty("completed").GetInt32());
        Assert.Equal("b", current.GetProperty("currentFile").GetString());
    }

    [Test]
    public void Old_runs_are_pruned_so_the_folder_cannot_grow_without_bound()
    {
        Directory.CreateDirectory(RunsDir);
        for (int i = 0; i < 30; i++)
            File.WriteAllText(Path.Combine(RunsDir, $"run-2020010{i / 10}-0000{i % 10:00}.jsonl"), "{}\n");

        var j = RunJournal.Start(_dir, "download", 1)!;
        Assert.True(Directory.GetFiles(RunsDir, "run-*.jsonl").Length <= 20, "pruned to the cap");
        Assert.True(File.Exists(j.EventsPath), "the NEW run is never the one pruned");
    }

    [Test]
    public void The_reader_reports_a_failed_run_and_speaks_the_shared_vocabulary()
    {
        var j = RunJournal.Start(_dir, "download", 2)!;
        j.File("a", ok: false, bytes: 0, error: "disk full");
        j.Finish(Grog.Core.Scheduling.ScheduleRunOutcome.Partial);

        var o = RunJournalReader.ReadLastFailure(_dir);
        Assert.True(o is { Failed: 1 }, "one failure reported");
        Assert.Equal(Grog.Core.Scheduling.ScheduleRunOutcome.Partial, o!.Value.State!.Value,
            "the state reads back as the enum, not a journal-private word");
        Assert.Equal("disk full", o.Value.Reason, "with the first failure's message as the reason");
    }

    [Test]
    public void A_summary_written_by_an_older_build_still_reads()
    {
        // Older journals said "completed-with-failures"; the shared vocabulary calls that Partial.
        Directory.CreateDirectory(RunsDir);
        File.WriteAllText(Path.Combine(RunsDir, "current.json"),
            "{\"runId\":\"x\",\"state\":\"completed-with-failures\",\"failed\":3}");

        var o = RunJournalReader.ReadLastFailure(_dir);
        Assert.Equal(Grog.Core.Scheduling.ScheduleRunOutcome.Partial, o!.Value.State!.Value,
            "the legacy word maps to Partial");
        Assert.Equal(3, o.Value.Failed);
    }

    [Test]
    public void The_reader_opens_the_events_file_while_a_writer_still_holds_it()
    {
        var j = RunJournal.Start(_dir, "download", 2)!;
        j.File("a", ok: false, bytes: 0, error: "disk full");
        // A second writer-mode handle stands in for the run still in flight; File.ReadLines would be denied on Windows.
        using var held = new FileStream(j.EventsPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        var lines = RunJournalReader.ReadSharedLines(j.EventsPath).ToList();
        Assert.True(lines.Count >= 1, "the lines already flushed are readable");
        Assert.Null(RunJournalReader.ReadLastFailure(_dir), "a run still writing is not the LAST run (1283)");
        var o = RunJournalReader.ReadLastFailure(_dir, readUnfinished: true);
        Assert.Equal("disk full", o!.Value.Reason, "the failure reason is read past the open writer");
    }

    // 1283 (QA 09-30 C5): the journal counts failed FILES: two strikes on one file are one, and a file that fails then
    // lands is none.
    [Test]
    public void Failed_counts_files_not_strikes()
    {
        var j = RunJournal.Start(_dir, "download", 3)!;
        j.File("a", ok: false, bytes: 0, error: "x", gogId: 1, fileKey: "a");
        j.File("a", ok: false, bytes: 0, error: "x", gogId: 1, fileKey: "a");
        j.File("b", ok: false, bytes: 0, error: "x", gogId: 2, fileKey: "b");
        j.File("b", ok: true, bytes: 5, gogId: 2, fileKey: "b");
        j.Finish(Grog.Core.Scheduling.ScheduleRunOutcome.Partial);
        var o = RunJournalReader.ReadLastFailure(_dir)!.Value;
        Assert.Equal(1, o.Failed, "a twice, b landed");
        Assert.Equal(1, o.FailedFiles().Distinct().Count(), "and the keys name a once");
    }

    [Test]
    public void A_run_with_no_failures_is_not_news()
    {
        var j = RunJournal.Start(_dir, "download", 1)!;
        j.File("a", ok: true, bytes: 1);
        j.Finish(Grog.Core.Scheduling.ScheduleRunOutcome.Completed);
        Assert.Null(RunJournalReader.ReadLastFailure(_dir), "callers only care about the bad news");
    }
}
