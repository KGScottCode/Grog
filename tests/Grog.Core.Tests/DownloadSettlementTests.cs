// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using Grog.Core.Download;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Sync;
using Grog.Core.Tests.Framework;

namespace Grog.Core.Tests;

[Trait("download")]
public sealed class DownloadSettlementTests
{
    private static (LibraryManifest M, GameFile F, DownloadTask T) Rig(DownloadTaskState state, string? refusal = null)
    {
        var f = new GameFile { GameGogId = 7, FileKey = "/k", Name = "Setup", HasPartial = true, PartialBytes = 5 };
        var m = new LibraryManifest();
        m.Items.Add(new LibraryItem { GogId = 7, Title = "G", Files = { f } });
        m.Downloads.Enqueue(7, "/k");
        var t = new DownloadTask { File = f, GameTitle = "G", State = state, UnavailableReason = refusal };
        return (m, f, t);
    }

    [Test]
    public void Completed_clears_strikes_and_partial_and_leaves_the_queue()
    {
        var (m, f, t) = Rig(DownloadTaskState.Completed); f.FailedAttempts = 2;
        var (o, changed) = DownloadSettlement.Settle(m, t, f);
        Assert.Equal(DownloadSettlement.Outcome.Completed, o, "outcome");
        Assert.True(changed && m.Downloads.Snapshot().Count == 0, "queue entry removed");
        Assert.True(f.FailedAttempts == 0 && !f.HasPartial && f.PartialBytes is null, "record reset");
    }

    [Test]
    public void A_late_failure_on_a_landed_file_strikes_nothing()
    {
        // (09-26) A duplicate attempt failing after the file landed: no strike, partial flag dropped, queue untouched.
        var (m, f, t) = Rig(DownloadTaskState.Failed); f.State = FileState.Verified;
        var (o, changed) = DownloadSettlement.Settle(m, t, f);
        Assert.Equal(DownloadSettlement.Outcome.NotFinished, o, "outcome");
        Assert.True(changed, "partial flag cleared is a change");
        Assert.True(f.FailedAttempts == 0 && !f.HasPartial && f.PartialBytes is null, "no strike, no partial");
        Assert.Equal(FileState.Verified, f.State, "still verified");
    }

    [Test]
    public void A_refusal_is_Unavailable_with_no_strike()
    {
        var (m, f, t) = Rig(DownloadTaskState.Failed, "Not for your region"); f.FailedAttempts = 2;
        var (o, _) = DownloadSettlement.Settle(m, t, f);
        Assert.Equal(DownloadSettlement.Outcome.Unavailable, o, "outcome");
        Assert.Equal(FileState.Unavailable, f.State, "state");
        Assert.Equal("Not for your region", f.UnavailableReason, "GOG's words kept");
        Assert.Equal(0, f.FailedAttempts, "no strike");
        Assert.Equal(0, m.Downloads.Snapshot().Count, "left the queue");
    }

    [Test]
    public void Failures_strike_then_condemn_on_the_third()
    {
        var (m, f, t) = Rig(DownloadTaskState.Failed);
        Assert.Equal(DownloadSettlement.Outcome.WillRetry, DownloadSettlement.Settle(m, t, f).Outcome, "1");
        Assert.Equal(DownloadSettlement.Outcome.WillRetry, DownloadSettlement.Settle(m, t, f).Outcome, "2");
        Assert.Equal(1, m.Downloads.Snapshot().Count, "still queued while retrying");
        Assert.Equal(DownloadSettlement.Outcome.GaveUp, DownloadSettlement.Settle(m, t, f).Outcome, "3");
        Assert.Equal(FileState.Corrupt, f.State, "condemned");
        Assert.Equal(0, m.Downloads.Snapshot().Count, "out of the queue");
    }

    [Test]
    public void RequeueFresh_lifts_Corrupt_and_forgets_the_partial()
    {
        var (m, f, _) = Rig(DownloadTaskState.Failed); m.Downloads.Remove(7, "/k");
        f.State = FileState.Corrupt; f.FailedAttempts = RetryPolicy.MaxAttempts;
        DownloadSettlement.RequeueFresh(m, 7, f);
        Assert.True(f.State == FileState.NotBackedUp && f.FailedAttempts == 0 && !f.HasPartial, "fresh");
        Assert.Equal(1, m.Downloads.Snapshot().Count, "queued again");
    }

    /// <summary>(QA 09-19) The DRIVE refused the bytes: no strike however often it happens (three full-disk runs
    /// condemned intact files as Corrupt), the file stays queued with its partial, flagged won't-fit.</summary>
    [Test]
    public void A_disk_full_failure_takes_no_strike_and_flags_the_row_wont_fit()
    {
        var (m, f, t) = Rig(DownloadTaskState.Failed); t.DiskFull = true;
        for (int n = 0; n < 5; n++) Assert.Equal(DownloadSettlement.Outcome.NoRoom, DownloadSettlement.Settle(m, t, f).Outcome);
        Assert.Equal(0, f.FailedAttempts, "no strike");
        Assert.True(f.State != FileState.Corrupt && f.HasPartial, "not condemned, partial kept");
        var q = m.Downloads.Snapshot();
        Assert.True(q.Count == 1 && !q[0].Fits, "still queued, flagged won't fit");

        var io = new System.IO.IOException("disk full", unchecked((int)0x80070070));
        Assert.True(DownloadEngine.IsDiskFull(io));
        Assert.True(DownloadEngine.IsDiskFull(new System.Exception("wrapped", io)));
        Assert.False(DownloadEngine.IsDiskFull(new System.IO.IOException("sharing violation", unchecked((int)0x80070020))));
    }

    /// <summary>(walk 09-19) Try Again on a file that merely FAILED keeps its resumable partial: clearing the record
    /// left a 4 GiB .part on the drive that nothing owned.</summary>
    [Test]
    public void RequeueFresh_keeps_the_partial_of_a_file_that_was_not_condemned()
    {
        var (m, f, _) = Rig(DownloadTaskState.Failed); f.State = FileState.NotBackedUp; f.FailedAttempts = 1;
        DownloadSettlement.RequeueFresh(m, 7, f);
        Assert.True(f.HasPartial && f.PartialBytes == 5, "still resumable");
        Assert.Equal(0, f.FailedAttempts);
    }

    /// <summary>(QA 09-19) The .part name no longer depends on the drive's path (a stick re-plugged under another
    /// letter orphaned its partial); a root-keyed name from an older build is still found; and the sweep spares
    /// what a record owns while taking what nothing does.</summary>
    [Test]
    public void Part_names_survive_a_path_change_and_the_sweep_is_owner_aware()
    {
        var a = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "grog-part-a-" + System.Guid.NewGuid().ToString("N"));
        var b = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "grog-part-b-" + System.Guid.NewGuid().ToString("N"));
        try
        {
            var f = new GameFile { GameGogId = 7, FileKey = "/k", Name = "Setup", HasPartial = true };
            Assert.Equal(System.IO.Path.GetFileName(DownloadEngine.PartialPathFor(a, f)), System.IO.Path.GetFileName(DownloadEngine.PartialPathFor(b, f)));

            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(a, ".grog-tmp"));
            var owned = DownloadEngine.PartialPathFor(a, f);
            var orphan = System.IO.Path.Combine(a, ".grog-tmp", "0123456789ABCDEF.part");
            var fresh = System.IO.Path.Combine(a, ".grog-tmp", "FEDCBA9876543210.part");
            foreach (var p in new[] { owned, orphan, fresh }) System.IO.File.WriteAllText(p, "x");
            var old = System.DateTime.UtcNow.AddHours(-1);
            System.IO.File.SetLastWriteTimeUtc(owned, old); System.IO.File.SetLastWriteTimeUtc(orphan, old);

            Assert.Equal(1, DownloadEngine.SweepOrphanPartials(new[] { a }, new[] { f }));
            Assert.True(System.IO.File.Exists(owned), "owned by a record: kept");
            Assert.False(System.IO.File.Exists(orphan), "owned by nothing: gone");
            Assert.True(System.IO.File.Exists(fresh), "younger than ten minutes: another process may be writing it");
        }
        finally { foreach (var d in new[] { a, b }) try { System.IO.Directory.Delete(d, true); } catch { } }
    }
}
