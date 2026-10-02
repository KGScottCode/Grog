// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System.Linq;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Runs;
using Grog.Core.Volumes;
using Grog.Core.Tests.Framework;

namespace Grog.Core.Tests;

/// <summary>(09-16) The queue moves offered around the won't-fit line: where "the bottom of the white group"
/// is, whether a red row could be lifted, and the greedy lift. All pure over a snapshot; the planner's own
/// rules (routed root first, spill only outside Manual), so a move offered is never one the next re-plan undoes.</summary>
[NewBatch]
[Trait("placement")]
public sealed class QueueShaperTests
{
    private static GameFile F(long game, string key, long size, bool partial = false, long partialBytes = 0)
        => new() { GameGogId = game, FileKey = key, Name = key, Kind = FileKind.Installer, State = FileState.NotBackedUp,
                   ExpectedSizeBytes = size, Os = "windows", Language = "English", HasPartial = partial, PartialBytes = partial ? partialBytes : null };

    private static LibraryManifest Lib()
    {
        var m = new LibraryManifest();
        m.Items.Add(new LibraryItem { GogId = 1, Title = "A", Slug = "a", Type = ProductType.Game, Files = { F(1, "a1", 10), F(1, "a2", 100) } });
        m.Items.Add(new LibraryItem { GogId = 2, Title = "B", Slug = "b", Type = ProductType.Game, Files = { F(2, "b1", 10), F(2, "b2", 30) } });
        m.Roots.Add(new BackupRoot { Id = "p", Label = "Primary" });
        m.Roots.Add(new BackupRoot { Id = "s", Label = "Secondary" });
        m.PrimaryRootId = "p";
        return m;
    }

    private sealed class MemStore : IManifestStore
    {
        public MemStore(LibraryManifest m) => Current = m;
        public LibraryManifest Current { get; }
        public ManifestGate Gate { get; } = new();
        public System.Threading.Tasks.Task LoadAsync(System.Threading.CancellationToken ct = default) => System.Threading.Tasks.Task.CompletedTask;
        public System.Threading.Tasks.Task SaveAsync(System.Threading.CancellationToken ct = default) => System.Threading.Tasks.Task.CompletedTask;
    }

    private static DeviceSpace[] Room(long p, long s) => new[] { new DeviceSpace("p", p, true), new DeviceSpace("s", s, true) };

    /// <summary>(09-19) The row says "too big for this drive" only when a per-file ceiling is what stops the file:
    /// over every online drive's ceiling, or over the ceiling of a drive that has the room. A full drive with no
    /// ceiling is plain "won't fit".</summary>
    [Test]
    public void FileLimitBlocking_names_the_ceiling_only_when_the_ceiling_is_the_reason()
    {
        const long fat = 4294967295L;
        var big = F(1, "big", 5_000_000_000L); var small = F(1, "small", 1_000_000_000L);
        var stick = new DeviceSpace("p", 10_000_000_000L, true) { MaxFileBytes = fat };
        Assert.Equal("FAT32", QueueShaper.FileLimitBlocking(big, new[] { stick }), "room, but over the ceiling");
        Assert.Null(QueueShaper.FileLimitBlocking(small, new[] { stick with { FreeBytes = 5 } }), "under the ceiling: just full");
        Assert.Equal("FAT32", QueueShaper.FileLimitBlocking(big, new[] { stick with { FreeBytes = 5 } }), "full AND over: fits nowhere here at any free space");
        Assert.Equal("FAT32", QueueShaper.FileLimitBlocking(big, new[] { stick, new DeviceSpace("s", 5, true) }), "the stick has the room; the ceiling is why");
        Assert.Null(QueueShaper.FileLimitBlocking(big, new[] { stick with { FreeBytes = 5 }, new DeviceSpace("s", 5, true) }), "both full, one without a ceiling: won't fit");
        Assert.Null(QueueShaper.FileLimitBlocking(big, new[] { stick with { IsOnline = false } }), "an offline drive says nothing");
    }

    /// <summary>(09-19) GOG's "4 GB" label is 4 GiB exactly, one byte over FAT32's ceiling, on a part cut to fit under
    /// it. A label inside the rounding slack is planned as fitting and is unsettled until the CDN's length is known;
    /// a real length answers exactly, either way.</summary>
    [Test]
    public void A_rounded_label_at_the_ceiling_fits_until_the_real_size_says_otherwise()
    {
        const long fat = 4294967295L;
        var stick = new[] { new DeviceSpace("p", 10_000_000_000L, true) { MaxFileBytes = fat } };
        var m = Lib();
        var label = F(1, "part2", 4294967296L);
        Assert.True(CeilingSizeCheck.IsUnsettled(label, fat), "label at the ceiling, no real size");
        Assert.True(QueueShaper.CouldFit(m, label, stick), "planned as fitting");
        Assert.Null(QueueShaper.FileLimitBlocking(label, stick), "and never called too big");
        label.WireSizeBytes = 4294967296L;
        Assert.False(CeilingSizeCheck.IsUnsettled(label, fat), "settled");
        Assert.False(QueueShaper.CouldFit(m, label, stick), "a REAL 4 GiB is one byte over");
        Assert.Equal("FAT32", QueueShaper.FileLimitBlocking(label, stick));
        label.WireSizeBytes = 4_290_000_000L;
        Assert.True(QueueShaper.CouldFit(m, label, stick), "a real length under the ceiling fits");
        var far = F(1, "mac", 6_549_825_126L);
        Assert.False(CeilingSizeCheck.IsUnsettled(far, fat), "a 6.1 GB label is not in doubt");
        Assert.False(QueueShaper.CouldFit(m, far, stick));
        Assert.Equal(1, CeilingSizeCheck.Unsettled(new[] { F(1, "x", 4294967296L), far, F(1, "y", 1000) }, stick).Count);
        Assert.Equal(0, CeilingSizeCheck.Unsettled(new[] { F(1, "x", 4294967296L) }, Room(10_000_000_000L, 0)).Count, "no ceiling, nothing to ask");
    }

    [Test]
    public void CeilingSizeCheck_records_the_real_length_and_survives_a_failed_ask()
    {
        var store = new MemStore(Lib());
        var a = F(1, "a", 4294967296L); var b = F(1, "b", 4294967296L);
        int learned = CeilingSizeCheck.ResolveAsync(store, new[] { a, b },
            (f, _) => f.FileKey == "a" ? System.Threading.Tasks.Task.FromResult<long?>(4_293_000_000L) : throw new System.Net.Http.HttpRequestException("offline"),
            default).GetAwaiter().GetResult();
        Assert.Equal(1, learned);
        Assert.Equal(4_293_000_000L, a.WireSizeBytes);
        Assert.Null(b.WireSizeBytes, "unasked stays unsettled");
    }

    /// <summary>KeeperFX at slot 11 (owner-measured): fitting and red rows interleave, and the slot is after
    /// the LAST fitting row, not the first red one.</summary>
    [Test]
    public void EndOfFittingBlock_is_after_the_last_fitting_row_when_rows_interleave()
    {
        var m = Lib();
        m.Downloads.Enqueue(1, "a1", "p", fits: true);
        m.Downloads.Enqueue(1, "a2", "p", fits: false);
        m.Downloads.Enqueue(2, "b1", "p", fits: true);
        m.Downloads.Enqueue(2, "b2", "p", fits: false);
        Assert.Equal(3, QueueShaper.EndOfFittingBlock(m.Downloads.Snapshot()), "after b1 (index 2)");
        Assert.Equal(1, QueueShaper.EndOfFittingBlock(m.Downloads.Snapshot(), (2, "b1")), "moving b1 itself: after a1, in post-removal space");
        Assert.Equal(-1, QueueShaper.EndOfFittingBlock(m.Downloads.Snapshot().Where(q => !q.Fits).ToList()), "nothing fits");
    }

    [Test]
    public void CouldFit_uses_the_routed_root_and_does_not_spill_in_Manual_mode()
    {
        var m = Lib();
        m.Routing.SetRole(ContentRole.Games, "s");   // installers routed to the secondary
        var big = m.Items[0].Files[1];   // a2, 100
        Assert.True(QueueShaper.CouldFit(m, big, Room(500, 2)), "Overflow: spills to the primary");
        m.Routing.Mode = RoutingMode.Manual;
        Assert.False(QueueShaper.CouldFit(m, big, Room(500, 2)), "Manual: the routed drive has no room, no spill");
        Assert.True(QueueShaper.CouldFit(m, big, Room(0, 100)), "Manual: the routed drive has room");
    }

    [Test]
    public void CouldFit_prices_a_partial_at_what_it_still_needs()
    {
        var m = Lib();
        var f = F(1, "a3", 100, partial: true, partialBytes: 90);
        m.Items[0].Files.Add(f);
        Assert.True(QueueShaper.CouldFit(m, f, Room(10, 0)), "100 - 90 on disk = 10 needed");
        Assert.False(QueueShaper.CouldFit(m, f, Room(9, 0)));
    }

    /// <summary>Greedy, in queue order, spending each device as the planner would; the lifted rows keep their
    /// relative order and land right after the last fitting row. A re-plan over the result flips nothing back.</summary>
    [Test]
    public void PullUpWhatFits_lifts_in_order_and_is_stable_under_a_replan()
    {
        var m = Lib();
        m.Downloads.Enqueue(1, "a2", "p", fits: false);   // 100: too big
        m.Downloads.Enqueue(1, "a1", "p", fits: true);    // 10: fits
        m.Downloads.Enqueue(2, "b2", "p", fits: false);   // 30
        m.Downloads.Enqueue(2, "b1", "p", fits: false);   // 10
        var (order, pulled) = QueueShaper.PullUpWhatFits(m, m.Downloads.Snapshot(), Room(40, 0));
        Assert.Equal("b2,b1", string.Join(",", pulled.Select(k => k.FileKey)), "30 then 10 fit in 40 (100 never does)");
        Assert.Equal("a2,a1,b2,b1", string.Join(",", order.Select(k => k.FileKey)), "after the last fitting row, in order; a2 keeps its slot");
        m.Downloads.SetOrder(order);
        // The plan over the new order with the ORIGINAL room (40 + the 10 a1 already reserved) agrees.
        var plan = PlacementPlanner.PlanQueueDetailed(
            order.Select(k => m.ItemById(k.GogId)!.Files.First(f => f.FileKey == k.FileKey)).ToList(),
            Room(50, 0), m.Routing, "p", m.Routing.Mode, m.ExtrasLayout);
        Assert.Equal("False,True,True,True", string.Join(",", plan.Placements.Select(p => p.Fits)), "nothing pulled flips back");
    }

    // 1283 (QA 09-30 A6): the pull-up spends the planner's cost (whole size off the partial's volume, plus the per-file
    // overhead), not the bare remainder, so what it lifts the next plan keeps.
    [Test]
    public void PullUpWhatFits_spends_the_planners_cost_not_the_bare_remainder()
    {
        var m = Lib();
        var b2 = m.ItemById(2)!.Files.First(f => f.FileKey == "b2");   // 30
        b2.HasPartial = true; b2.PartialBytes = 25; b2.PartialRootId = "s";   // 5 still needed, but the partial is on s
        m.Downloads.Enqueue(1, "a1", "p", fits: true);    // 10
        m.Downloads.Enqueue(2, "b2", "p", fits: false);
        m.Downloads.Enqueue(2, "b1", "p", fits: false);   // 10
        var room = new[] { new DeviceSpace("p", 35, true) { ClusterBytes = 4 }, new DeviceSpace("s", 0, true) { ClusterBytes = 4 } };
        var (_, pulled) = QueueShaper.PullUpWhatFits(m, m.Downloads.Snapshot(), room);
        // b2 costs 32 on p (30 rounded to 4-byte units; its partial sits on s): 35 - 32 = 3 left, b1 (12) does not fit.
        Assert.Equal("b2", string.Join(",", pulled.Select(k => k.FileKey)), "b1 is not pulled on room b2 really spends");
    }

    [Test]
    public void PullUpWhatFits_pulls_nothing_when_nothing_fits_or_nothing_fitted()
    {
        var m = Lib();
        m.Downloads.Enqueue(1, "a2", "p", fits: false);
        var (order, pulled) = QueueShaper.PullUpWhatFits(m, m.Downloads.Snapshot(), Room(500, 0));
        Assert.Equal(0, pulled.Count, "no fitting row to anchor under");
        Assert.Equal(1, order.Count);
        m.Downloads.Enqueue(1, "a1", "p", fits: true);
        (order, pulled) = QueueShaper.PullUpWhatFits(m, m.Downloads.Snapshot(), Room(5, 0));
        Assert.Equal(0, pulled.Count, "nothing below the line fits in 5");
    }
}
