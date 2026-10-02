// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Runs;
using Grog.Core.Tests.Framework;

/// <summary>The one fix sequence (S2.1, 09-08) behind the App's Fix / Try again / Re-download and `grogcli fix`.</summary>
[NewBatch]
[Trait("run")]
public class FixRunTests
{
    private sealed class MemoryStore : IManifestStore
    {
        public LibraryManifest Current { get; } = new();
        public ManifestGate Gate { get; } = new();
        public int Saves;
        public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveAsync(CancellationToken ct = default) { Saves++; return Task.CompletedTask; }
    }

    private static GameFile Gf(long gogId, string key, FileState state, int failed = 0)
        => new() { GameGogId = gogId, FileKey = key, Name = key, Kind = FileKind.Installer, State = state, FailedAttempts = failed,
                   LocalRelativePath = state == FileState.NotBackedUp ? null : $"Games/g{gogId}/{key}" };

    /// <summary>Two products covering every state the predicate must sort: a strike on a gap (retryable), a
    /// strike on a Corrupt (verify's verdict, not a retry), Unavailable (never), a present file with old
    /// strikes (healed), a plain Missing with no strikes (a fix, not a retry), and one queued already.</summary>
    private static MemoryStore Library()
    {
        var s = new MemoryStore();
        var a = new LibraryItem { GogId = 1, Title = "A", Slug = "a" };
        a.Files.Add(Gf(1, "a-failed-gap", FileState.NotBackedUp, failed: 3));
        a.Files.Add(Gf(1, "a-failed-missing", FileState.Missing, failed: 1));
        a.Files.Add(Gf(1, "a-corrupt-struck", FileState.Corrupt, failed: 2));
        a.Files.Add(Gf(1, "a-present-old-strikes", FileState.Present, failed: 2));
        var b = new LibraryItem { GogId = 2, Title = "B", Slug = "b" };
        b.Files.Add(Gf(2, "b-unavailable", FileState.Unavailable, failed: 5));
        b.Files.Add(Gf(2, "b-missing", FileState.Missing));
        b.Files.Add(Gf(2, "b-outdated-struck", FileState.Outdated, failed: 1));
        b.Files.Add(Gf(2, "b-verified", FileState.Verified));
        s.Current.Items.Add(a); s.Current.Items.Add(b);
        // A backlog already in the queue: the fix must land IN FRONT of it.
        s.Current.Downloads.Enqueue(2, "b-verified");
        return s;
    }

    [Test]
    void FailedDownload_Predicate_IsExactly_StrikesOnAGap()
    {
        var s = Library();
        var failed = s.Current.Items.SelectMany(i => i.Files).Where(FixRun.IsFailedDownload).Select(f => f.FileKey).ToList();
        Assert.Equal("a-failed-gap,a-failed-missing,b-outdated-struck", string.Join(",", failed),
            "NotBackedUp/Missing/Outdated with strikes; never Corrupt, Unavailable or a present file");
    }

    [Test]
    async Task Failed_Selector_Requeues_ThePredicateSet_AtTheFront_InOrder_AndSaves()
    {
        var s = Library();
        var r = await FixRun.RequeueAsync(s, FixSelector.Failed);

        Assert.Equal(3, r.Count, "the three retryable failures");
        var queue = s.Current.Downloads.Snapshot().Select(q => q.FileKey).ToList();
        Assert.Equal("a-failed-gap,a-failed-missing,b-outdated-struck,b-verified", string.Join(",", queue),
            "fix set first, in manifest order, then the backlog");
        Assert.Equal(string.Join(",", r.Keys.Select(k => k.FileKey)), string.Join(",", queue.Take(3)), "result keys are the queue head");
        foreach (var f in s.Current.Items.SelectMany(i => i.Files).Where(f => r.Keys.Contains((f.GameGogId, f.FileKey))))
        {
            Assert.Equal(FileState.NotBackedUp, f.State, $"{f.FileKey}: reset for a fresh fetch");
            Assert.Equal(0, f.FailedAttempts, $"{f.FileKey}: strikes cleared, or it is re-condemned at once");
        }
        Assert.Equal(FileState.Corrupt, s.Current.Items[0].Files[2].State, "Corrupt is the Fix flow's, not a retry");
        Assert.Equal(1, s.Saves, "saved inside");
        Assert.NotEqual(BackupStatus.Unknown, s.Current.Items[0].Status, "touched products rolled up");
    }

    [Test]
    async Task Exclude_KeepsAnActiveFileOutOfTheRetry()
    {
        var s = Library();
        var active = new HashSet<(long, string)> { (1, "a-failed-gap") };
        var r = await FixRun.RequeueAsync(s, FixSelector.Failed, active);

        Assert.Equal(2, r.Count, "the file a worker is mid-attempt on is left alone");
        Assert.False(r.Keys.Contains((1, "a-failed-gap")), "not in the result");
        Assert.Equal(3, s.Current.Items[0].Files[0].FailedAttempts, "its strikes untouched");
        Assert.False(s.Current.Downloads.Contains(1, "a-failed-gap"), "not queued behind the worker's back");
    }

    [Test]
    async Task CorruptOrMissing_Selector_IsTheFixFlow()
    {
        var s = Library();
        var r = await FixRun.RequeueAsync(s, FixSelector.CorruptOrMissing);
        Assert.Equal("a-failed-missing,a-corrupt-struck,b-missing", string.Join(",", r.Keys.Select(k => k.FileKey)),
            "every Corrupt and Missing, strikes or not; never Unavailable");
        Assert.Equal(FileState.NotBackedUp, s.Current.Items[0].Files[2].State, "Corrupt lifted for a fresh copy");
    }

    [Test]
    async Task Game_And_File_Selectors_TargetExactly()
    {
        var s = Library();
        var game = await FixRun.RequeueAsync(s, FixSelector.Game(1));
        Assert.Equal(4, game.Count, "every file of the game, present ones included");
        Assert.True(game.Keys.All(k => k.GogId == 1), "only that game");

        var s2 = Library();
        var one = await FixRun.RequeueAsync(s2, FixSelector.File(2, "b-verified"));
        Assert.Equal(1, one.Count, "the one file");
        Assert.Equal("b-verified", s2.Current.Downloads.Snapshot()[0].FileKey, "moved to the front");
        Assert.Equal(1, s2.Current.Downloads.Snapshot().Count, "re-queueing a queued file does not duplicate it");
    }

    [Test]
    async Task NothingMatching_Requeues_Nothing_AndDoesNotSave()
    {
        var s = new MemoryStore();
        var r = await FixRun.RequeueAsync(s, FixSelector.Corrupt);
        Assert.Equal(0, r.Count, "empty");
        Assert.Equal(0, s.Saves, "no save for a no-op");
        Assert.Equal(0, FixRun.Count(s, FixSelector.CorruptOrMissing), "count agrees");
    }

    // 1283 (QA 09-30 C1): the Health card's Try Again names the run's failed files by key. A named Corrupt file is
    // retried (strikes reset, state lifted); a named present file and a named Unavailable one are left alone; the
    // strike-on-a-gap set still comes along.
    [Test]
    async Task FailedOrKeys_Selector_Retries_Named_Corrupt_Files_But_Never_Present_Or_Unavailable()
    {
        var s = Library();
        var sel = FixSelector.FailedOrKeys(new[] { (1L, "a-corrupt-struck"), (1L, "a-present-old-strikes"), (2L, "b-unavailable") });
        var r = await FixRun.RequeueAsync(s, sel);
        var keys = r.Keys.Select(k => k.FileKey).OrderBy(k => k).ToList();
        Assert.Equal("a-corrupt-struck,a-failed-gap,a-failed-missing,b-outdated-struck", string.Join(",", keys),
            "the named Corrupt file joins the strike set; present and Unavailable stay out");
        var corrupt = s.Current.ItemById(1)!.Files.First(f => f.FileKey == "a-corrupt-struck");
        Assert.Equal(0, corrupt.FailedAttempts, "strikes reset");
        Assert.True(corrupt.State != FileState.Corrupt, "Corrupt lifted for a fresh fetch");
    }
}
