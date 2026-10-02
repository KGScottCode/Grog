// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System.Linq;
using Grog.Core.Download;
using Grog.Core.Tests.Framework;

[NewBatch]
[Trait("dlqueue")]
public class DownloadQueuePersistenceTests
{
    [Test] void EnqueueIsIdempotent()
    {
        var q = new DownloadQueue();
        Assert.True(q.Enqueue(1, "a"), "first add");
        Assert.True(!q.Enqueue(1, "a"), "duplicate rejected");
        Assert.Equal(1, q.Items.Count, "one item");
    }

    [Test] void EnqueuePreservesOrder()
    {
        var q = new DownloadQueue();
        q.Enqueue(1, "a"); q.Enqueue(1, "b"); q.Enqueue(2, "c");
        Assert.Equal("a,b,c", string.Join(",", q.Items.Select(i => i.FileKey)), "insertion order");
    }

    [Test] void RemoveDropsCompletedFile()
    {
        var q = new DownloadQueue();
        q.Enqueue(1, "a"); q.Enqueue(1, "b");
        Assert.True(q.Remove(1, "a"), "removed");
        Assert.True(!q.Contains(1, "a"), "gone");
        Assert.True(q.Contains(1, "b"), "sibling stays");
        Assert.True(!q.Remove(1, "a"), "second remove is a no-op");
    }

    [Test] void MoveToReordersWithinQueue()
    {
        var q = new DownloadQueue();
        q.Enqueue(1, "a"); q.Enqueue(1, "b"); q.Enqueue(1, "c");
        q.MoveTo(1, "c", 0);   // pull last to front (pre-empt)
        Assert.Equal("c,a,b", string.Join(",", q.Items.Select(i => i.FileKey)), "c moved to front");
    }

    [Test] void MoveToUnknownIsNoOp()
    {
        var q = new DownloadQueue();
        q.Enqueue(1, "a");
        q.MoveTo(9, "z", 0);
        Assert.Equal("a", string.Join(",", q.Items.Select(i => i.FileKey)), "unchanged");
    }

    [Test] void ClearEmptiesAndUnpauses()
    {
        var q = new DownloadQueue { Paused = true };
        q.Enqueue(1, "a");
        q.Clear();
        Assert.True(q.IsEmpty, "empty");
        Assert.True(!q.Paused, "paused reset");
    }

    [Test] void TargetRootIdRoundTrips()
    {
        var q = new DownloadQueue();
        q.Enqueue(1, "a", "SECONDARY");
        Assert.Equal("SECONDARY", q.Items[0].TargetRootId, "placement carried for resume");
    }

    /// <summary>(09-13) A won't-fit flag survives save/load, and a queue written before the field existed loads
    /// as fitting, so the red rows are exactly the ones the last plan flagged and nothing older turns red.</summary>
    [Test] void FitsRoundTripsAndDefaultsTrue()
    {
        var q = new DownloadQueue();
        q.Enqueue(1, "a", "p", fits: false); q.Enqueue(1, "b", "p", fits: true);
        var json = System.Text.Json.JsonSerializer.Serialize(q);
        var back = System.Text.Json.JsonSerializer.Deserialize<DownloadQueue>(json)!;
        Assert.False(back.Items[0].Fits, "won't-fit survives the round trip");
        Assert.True(back.Items[1].Fits, "fits survives the round trip");

        var legacy = System.Text.Json.JsonSerializer.Deserialize<DownloadQueue>("{\"Items\":[{\"GogId\":1,\"FileKey\":\"a\"}],\"Paused\":false}")!;
        Assert.True(legacy.Items[0].Fits, "a pre-field entry loads as fitting");
    }

    /// <summary>(09-13) SetPlacement never appends: a re-plan that runs from a snapshot must not resurrect a file
    /// that completed or was cancelled in between.</summary>
    [Test] void SetPlacementDoesNotResurrectAnAbsentEntry()
    {
        var q = new DownloadQueue();
        q.Enqueue(1, "a", "p");
        Assert.True(q.SetPlacement(1, "a", "s", false), "present: updated");
        Assert.Equal("s", q.Items[0].TargetRootId); Assert.False(q.Items[0].Fits);
        Assert.False(q.SetPlacement(2, "gone", "p", true), "absent: refused");
        Assert.Equal(1, q.Items.Count, "and nothing was appended");
    }
}
