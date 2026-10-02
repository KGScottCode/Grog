// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Runs;
using Grog.Core.Tests.Framework;

namespace Grog.Core.Tests;

/// <summary>(1287) The background sizer prices queued files at their real byte count, once each, in queue order,
/// skipping what the engine is transferring, and stops on a dead connection.</summary>
[Trait("runs")]
public sealed class QueueSizerTests
{
    private static GameFile F(long game, string key, long label, long? wire = null)
        => new() { GameGogId = game, FileKey = key, Name = key, Kind = FileKind.Installer, State = FileState.NotBackedUp,
                   ExpectedSizeBytes = label, WireSizeBytes = wire, Os = "windows", Language = "English" };

    private sealed class MemStore : IManifestStore
    {
        public MemStore(LibraryManifest m) => Current = m;
        public LibraryManifest Current { get; }
        public ManifestGate Gate { get; } = new();
        public int Saves;
        public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveAsync(CancellationToken ct = default) { Saves++; return Task.CompletedTask; }
    }

    private static MemStore Lib()
    {
        var m = new LibraryManifest { PrimaryRootId = "p" };
        m.Roots.Add(new BackupRoot { Id = "p", Label = "Primary" });
        m.Items.Add(new LibraryItem { GogId = 1, Title = "A", Slug = "a", Type = ProductType.Game,
            Files = { F(1, "a1", 10_000_000), F(1, "a2", 20_000_000, wire: 19_500_000), F(1, "a3", 5_000_000) } });
        m.Items.Add(new LibraryItem { GogId = 2, Title = "B", Slug = "b", Type = ProductType.Game,
            Files = { F(2, "b1", 30_000_000), F(2, "b2", 1_000_000) } });
        m.Downloads.Enqueue(2, "b1"); m.Downloads.Enqueue(1, "a1"); m.Downloads.Enqueue(1, "a2"); m.Downloads.Enqueue(1, "a3");
        // b2 is NOT queued: never sized.
        return new MemStore(m);
    }

    private static async Task Drain(QueueSizer s) { for (int i = 0; i < 200 && s.IsRunning; i++) await Task.Delay(20); Assert.False(s.IsRunning, "the pass ends"); }

    [Test]
    public void Unsized_is_the_queued_files_still_at_their_label_in_queue_order()
    {
        var s = Lib();
        Assert.Equal("b1,a1,a3", string.Join(",", QueueSizer.Unsized(s.Current).Select(k => k.FileKey)), "a2 already sized; b2 not queued");
    }

    [Test]
    public async Task Sizes_land_on_the_records_once_in_queue_order_and_the_host_hears_per_batch()
    {
        var s = Lib();
        var asked = new List<string>(); var batches = new List<int>();
        var sizer = new QueueSizer(s, (key, _) => { asked.Add(key); return Task.FromResult<long?>(key.Length * 1_000_000L + 123); }, _ => false)
            { BatchSize = 2, Pace = TimeSpan.Zero };
        sizer.SizesLearned += n => batches.Add(n);
        sizer.Request();
        await Drain(sizer);
        Assert.Equal("b1,a1,a3", string.Join(",", asked), "queue order, sized files not asked");
        Assert.Equal(2_000_123, s.Current.ItemById(1)!.Files[0].WireSizeBytes, "a1 priced at bytes");
        Assert.Equal(19_500_000, s.Current.ItemById(1)!.Files[1].WireSizeBytes, "a2 untouched");
        Assert.Equal("2,1", string.Join(",", batches), "two batches: 2 then the trailing 1");
        Assert.Equal(0, QueueSizer.Unsized(s.Current).Count, "nothing left");

        asked.Clear();
        sizer.Request();
        await Drain(sizer);
        Assert.Equal(0, asked.Count, "a second request with nothing unsized asks GOG nothing");
    }

    [Test]
    public async Task A_file_the_engine_is_transferring_is_left_to_its_own_transfer()
    {
        var s = Lib();
        var asked = new List<string>();
        var sizer = new QueueSizer(s, (key, _) => { asked.Add(key); return Task.FromResult<long?>(42); }, key => key.FileKey == "a1") { Pace = TimeSpan.Zero };
        sizer.Request();
        await Drain(sizer);
        Assert.Equal("b1,a3", string.Join(",", asked));
        Assert.Null(s.Current.ItemById(1)!.Files[0].WireSizeBytes, "a1 keeps its label until its transfer learns the size");
    }

    [Test]
    public async Task Three_failures_in_a_row_stop_the_pass_and_a_null_answer_keeps_the_label()
    {
        var s = Lib();
        s.Current.Downloads.Enqueue(2, "b2");
        var asked = new List<string>(); var logs = new List<string>();
        var sizer = new QueueSizer(s, (key, _) => key == "b1" ? Task.FromResult<long?>(null) : throw new System.Net.Http.HttpRequestException("down"),
                                   _ => false, logs.Add) { Pace = TimeSpan.Zero };
        sizer.Request();
        await Drain(sizer);
        Assert.Null(s.Current.ItemById(2)!.Files[0].WireSizeBytes, "no answer: label kept");
        Assert.Equal(4, QueueSizer.Unsized(s.Current).Count, "nothing learned");
        Assert.True(logs.Any(l => l.StartsWith("Sizing paused")), string.Join(" | ", logs));
    }

    [Test]
    public async Task Stop_ends_the_pass_and_keeps_what_landed()
    {
        var s = Lib();
        var gate = new SemaphoreSlim(0);
        int n = 0;
        var sizer = new QueueSizer(s, async (key, ct) => { if (++n == 2) { await gate.WaitAsync(ct); } return 7; }, _ => false) { Pace = TimeSpan.Zero, BatchSize = 1 };
        sizer.Request();
        for (int i = 0; i < 100 && n < 2; i++) await Task.Delay(10);
        sizer.Stop();
        await Drain(sizer);
        Assert.Equal(7, s.Current.ItemById(2)!.Files[0].WireSizeBytes, "the first answer stays");
        Assert.Null(s.Current.ItemById(1)!.Files[0].WireSizeBytes, "the one in flight at Stop was not written");
    }
}
