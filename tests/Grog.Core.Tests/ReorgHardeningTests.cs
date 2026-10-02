// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Storage;
using Grog.Core.Tests.Framework;
using Grog.Core.Volumes;

/// <summary>Cross-drive copy hardening: live CopiedBytes, no orphaned .grogpart, size-verify against the real
/// source length, and a journal store with a .bak fallback.</summary>
[NewBatch]
[Trait("reorg")]
public sealed class ReorgHardeningTests
{
    sealed record Env(JsonManifestStore Store, string Root, string Root2, string ConfigDir);

    static Env NewSetup()
    {
        var root = Directory.CreateTempSubdirectory("grog-harden-").FullName;
        var root2 = Directory.CreateTempSubdirectory("grog-harden2-").FullName;
        var cfg = Path.Combine(root, "_cfg");
        Environment.SetEnvironmentVariable("GROG_CONFIG_DIR", cfg);
        var paths = GrogPaths.Resolve(root);
        var store = new JsonManifestStore(paths);
        store.LoadAsync().GetAwaiter().GetResult();
        _ = new BackupLayout(store.Current, root);
        return new Env(store, root, root2, paths.ConfigDir);
    }

    static void Cleanup(Env e)
    {
        try { Directory.Delete(e.Root, true); } catch { }
        try { Directory.Delete(e.Root2, true); } catch { }
    }

    // A cross-drive move of `bytes` random bytes; the manifest record carries the sizes the caller gives.
    static ReorgMove CrossDrive(Env e, int bytes, long? localSize, long? expectedSize, string? md5)
    {
        var item = new LibraryItem { GogId = 2, Title = "Archived", Slug = "archived" };
        e.Store.Current.Items.Add(item);
        var fromAbs = Path.Combine(e.Root, "archived", "big.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(fromAbs)!);
        var payload = new byte[bytes];
        new Random(42).NextBytes(payload);
        File.WriteAllBytes(fromAbs, payload);
        var f = new GameFile
        {
            GameGogId = 2, FileKey = "archived/big.bin", Kind = FileKind.Installer, Name = "big.bin",
            RootId = e.Store.Current.PrimaryRootId, LocalRelativePath = "archived/big.bin",
            State = FileState.Present, ExpectedSizeBytes = expectedSize, LocalSizeBytes = localSize, ExpectedMd5 = md5,
        };
        item.Files.Add(f);
        return new ReorgMove
        {
            GogId = 2, FileKey = f.FileKey, Title = "Archived",
            FromRel = "archived/big.bin", ToRel = "Games/archived/big.bin",
            ToRootId = "root2-id", FromAbs = fromAbs, ToAbs = Path.Combine(e.Root2, "Games", "archived", "big.bin"),
            SizeBytes = Grog.Core.Sync.Rollups.HeldBytesOf(f),   // the planner's size: LocalSizeBytes ?? label
            SizeIsLabel = f.LocalSizeBytes is null,               // as the planner sets it
            ExpectedMd5 = f.ExpectedMd5,
            Kind = ReorgMoveKind.CopyVerifyDelete,
        };
    }

    static ReorgRunner Runner(Env e) => new(e.Store, new ReorgJournalStore(e.ConfigDir));

    // ---- E4: CopiedBytes is written during the copy, so RemainingWrites shrinks as bytes land ----

    [Test]
    async Task MidCopy_RemainingWrites_ShrinksAsBytesLand()
    {
        var e = NewSetup();
        try
        {
            const int size = 2_500_000;   // three 1 MB chunks
            var mv = CrossDrive(e, size, size, size, null);
            var job = new ReorgJob { Reason = ReorgReason.Relocate, Moves = { mv } };
            var runner = Runner(e);
            var snapshots = new List<long>();
            runner.Progress += p =>
            {
                if (p.CurrentFileFraction > 0 && p.CurrentFileFraction < 1)
                    snapshots.Add(job.RemainingWrites().Single().Bytes);
            };

            var res = await runner.RunAsync(job, new ReorgControl());
            Assert.Equal(ReorgOutcome.Completed, res.Outcome);
            Assert.True(snapshots.Count >= 2, "progress fired mid-copy");
            Assert.True(snapshots.All(b => b > 0 && b < size), $"each mid-copy reservation is a proper part of the file: {string.Join(",", snapshots)}");
            Assert.True(snapshots.Zip(snapshots.Skip(1)).All(z => z.Second < z.First), "and it shrinks as chunks land");
            Assert.Equal(0L, mv.CopiedBytes, "settled: nothing left copied");
        }
        finally { Cleanup(e); }
    }

    // ---- E5: flush-to-disk before verify (fsync itself is not observable from a unit test) ----

    [Test]
    async Task CrossDrive_StillVerifiesAndDeletesSource()
    {
        var e = NewSetup();
        try
        {
            var mv = CrossDrive(e, 3_000, 3_000, 3_000, null);
            mv.ExpectedMd5 = await Grog.Core.Verify.Hashing.Md5Async(mv.FromAbs);
            var job = new ReorgJob { Reason = ReorgReason.Relocate, Moves = { mv } };
            var res = await Runner(e).RunAsync(job, new ReorgControl());
            Assert.Equal(ReorgOutcome.Completed, res.Outcome);
            Assert.True(File.Exists(mv.ToAbs), "landed");
            Assert.False(File.Exists(mv.FromAbs), "source deleted after verify");
            Assert.False(File.Exists(mv.ToAbs + ".grogpart"), "no part left");
        }
        finally { Cleanup(e); }
    }

    // ---- E6: a failed or stopped copy takes its .grogpart with it ----

    [Test]
    async Task IOExceptionMidCopy_LeavesNoPart()
    {
        var e = NewSetup();
        try
        {
            const int size = 2_500_000;
            var mv = CrossDrive(e, size, size, size, null);
            var job = new ReorgJob { Reason = ReorgReason.Relocate, Moves = { mv } };
            var runner = Runner(e);
            bool sawPart = false;
            runner.Progress += p =>
            {
                if (p.CurrentFileFraction <= 0 || p.CurrentFileFraction >= 1) return;
                sawPart |= File.Exists(mv.ToAbs + ".grogpart");
                throw new IOException("write error");   // surfaces from inside the copy loop
            };

            var res = await runner.RunAsync(job, new ReorgControl());
            Assert.True(sawPart, "the part existed when the copy blew up");
            Assert.False(File.Exists(mv.ToAbs + ".grogpart"), "part removed on the exception");
            Assert.False(File.Exists(mv.ToAbs), "nothing promoted");
            Assert.True(File.Exists(mv.FromAbs), "source untouched");
            Assert.Equal(ReorgMoveState.Failed, mv.State);
            Assert.Equal(0L, mv.CopiedBytes, "nothing counted as copied");
            Assert.Equal(1, res.Failed);
        }
        finally { Cleanup(e); }
    }

    [Test]
    async Task CancelMidCopy_LeavesNoPart()
    {
        var e = NewSetup();
        try
        {
            const int size = 2_500_000;
            var mv = CrossDrive(e, size, size, size, null);
            var job = new ReorgJob { Reason = ReorgReason.Relocate, Moves = { mv } };
            var runner = Runner(e);
            var control = new ReorgControl();
            runner.Progress += p => { if (p.CurrentFileFraction > 0 && p.CurrentFileFraction < 1) control.Cancel(); };

            var res = await runner.RunAsync(job, control);
            Assert.Equal(ReorgOutcome.Canceled, res.Outcome);
            Assert.False(File.Exists(mv.ToAbs + ".grogpart"), "part removed on cancel");
            Assert.True(File.Exists(mv.FromAbs), "source untouched");
            Assert.Equal(ReorgMoveState.Pending, mv.State, "the move is still open for a revert/discard decision");
        }
        finally { Cleanup(e); }
    }

    [Test]
    void DiscardParts_SweepsUnlandedMoves()
    {
        var dir = Directory.CreateTempSubdirectory("grog-parts-").FullName;
        try
        {
            var open = new ReorgMove { Kind = ReorgMoveKind.CopyVerifyDelete, ToAbs = Path.Combine(dir, "a.bin"), CopiedBytes = 5 };
            var done = new ReorgMove { Kind = ReorgMoveKind.CopyVerifyDelete, ToAbs = Path.Combine(dir, "b.bin"), State = ReorgMoveState.Done };
            File.WriteAllText(open.ToAbs + ".grogpart", "x");
            File.WriteAllText(done.ToAbs + ".grogpart", "x");   // a stray next to a landed file is not ours to judge
            var job = new ReorgJob { Moves = { open, done } };
            ReorgRunner.DiscardParts(job);
            Assert.False(File.Exists(open.ToAbs + ".grogpart"), "open move's part swept");
            Assert.Equal(0L, open.CopiedBytes);
            Assert.True(File.Exists(done.ToAbs + ".grogpart"), "settled move left alone");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ---- E11: no MD5 and no LocalSizeBytes -> verify against the source's real length, not the label ----

    [Test]
    async Task SizeVerify_UsesSourceLength_NotRoundedLabel()
    {
        var e = NewSetup();
        try
        {
            const int real = 100_000;
            var mv = CrossDrive(e, real, localSize: null, expectedSize: real + 3_000, md5: null);   // label off by 3 KB
            Assert.Equal(real + 3_000L, mv.SizeBytes, "the planner carried the label");
            Assert.True(mv.SizeIsLabel, "and flagged it as one");
            var job = new ReorgJob { Reason = ReorgReason.Relocate, Moves = { mv } };
            var res = await Runner(e).RunAsync(job, new ReorgControl());
            Assert.Equal(ReorgOutcome.Completed, res.Outcome, "a good copy verifies against the real source length");
            Assert.Equal(ReorgMoveState.Done, mv.State);
            Assert.True(File.Exists(mv.ToAbs), "landed");
            Assert.False(File.Exists(mv.FromAbs), "source deleted");
        }
        finally { Cleanup(e); }
    }

    [Test]
    async Task SizeVerify_RealRecordedSize_CatchesTruncatedSource()
    {
        var e = NewSetup();
        try
        {
            const int real = 100_000;
            var mv = CrossDrive(e, real - 4_096, localSize: real, expectedSize: real, md5: null);   // the source lost a tail since it was recorded
            Assert.False(mv.SizeIsLabel, "a real recorded size is not a label");
            var job = new ReorgJob { Reason = ReorgReason.Relocate, Moves = { mv } };
            var res = await Runner(e).RunAsync(job, new ReorgControl());
            Assert.NotEqual(ReorgMoveState.Done, mv.State, "a copy of a truncated source must not verify against its own length");
            Assert.True(File.Exists(mv.FromAbs), "source kept");
        }
        finally { Cleanup(e); }
    }

    // ---- R5: journal store keeps a .bak and falls back to it ----

    [Test]
    void Journal_TruncatedPrimary_LoadsFromBak()
    {
        var dir = Directory.CreateTempSubdirectory("grog-journal-").FullName;
        try
        {
            var store = new ReorgJournalStore(dir);
            var first = new ReorgJob { Id = "first-job", Reason = ReorgReason.Relocate };
            first.Moves.Add(new ReorgMove { FileKey = "a", Kind = ReorgMoveKind.Rename });
            store.Save(first);
            var second = new ReorgJob { Id = "second-job", Reason = ReorgReason.Relocate };
            store.Save(second);   // rolls the first save into .bak
            var path = Path.Combine(dir, ReorgJournalStore.FileName);
            Assert.True(File.Exists(path + ".bak"), "previous journal kept as .bak");
            Assert.False(File.Exists(path + ".tmp"), "temp gone after the rename");

            File.WriteAllText(path, File.ReadAllText(path)[..20]);   // truncated mid-write

            var back = new ReorgJournalStore(dir).Load();
            Assert.NotNull(back, "the .bak is used");
            Assert.Equal("first-job", back!.Id, "it is the previous journal");
            Assert.Equal(1, back.Moves.Count);
            Assert.True(File.Exists(path + ".corrupt"), "the bad primary is quarantined");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Test]
    void Journal_SaveAfterBakRecovery_DoesNotRollCorruptPrimaryIntoBak()
    {
        var dir = Directory.CreateTempSubdirectory("grog-journal-").FullName;
        try
        {
            var store = new ReorgJournalStore(dir);
            store.Save(new ReorgJob { Id = "good" });
            store.Save(new ReorgJob { Id = "newer" });
            var path = Path.Combine(dir, ReorgJournalStore.FileName);
            File.WriteAllText(path, "{ not json");

            var s2 = new ReorgJournalStore(dir);
            var back = s2.Load()!;
            Assert.Equal("good", back.Id, "recovered from .bak");
            s2.Save(back);
            Assert.Equal("good", new ReorgJournalStore(dir).Load()!.Id, "primary is the clean write");
            var bak = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path + ".bak"));
            Assert.Equal("good", bak.RootElement.GetProperty("Id").GetString(), "the corrupt primary never replaced the good .bak");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Test]
    void Journal_SaveThroughNewInstance_KeepsGoodBakWhenPrimaryIsCorrupt()
    {
        var dir = Directory.CreateTempSubdirectory("grog-journal-").FullName;
        try
        {
            var store = new ReorgJournalStore(dir);
            store.Save(new ReorgJob { Id = "good" });
            store.Save(new ReorgJob { Id = "newer" });
            var path = Path.Combine(dir, ReorgJournalStore.FileName);
            File.WriteAllText(path, "{ not json");

            // The App loads through one instance and saves through another: no Load on this one.
            new ReorgJournalStore(dir).Save(new ReorgJob { Id = "resumed" });
            Assert.Equal("resumed", new ReorgJournalStore(dir).Load()!.Id, "primary is the clean write");
            var bak = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path + ".bak"));
            Assert.Equal("good", bak.RootElement.GetProperty("Id").GetString(), "the corrupt primary never replaced the good .bak");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Test]
    void Journal_NoBak_UnreadablePrimary_IsNull()
    {
        var dir = Directory.CreateTempSubdirectory("grog-journal-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, ReorgJournalStore.FileName), "garbage");
            Assert.Null(new ReorgJournalStore(dir).Load(), "nothing to fall back to");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Test]
    void Journal_Delete_RemovesBakToo()
    {
        var dir = Directory.CreateTempSubdirectory("grog-journal-").FullName;
        try
        {
            var store = new ReorgJournalStore(dir);
            store.Save(new ReorgJob()); store.Save(new ReorgJob());
            store.Delete();
            Assert.False(File.Exists(Path.Combine(dir, ReorgJournalStore.FileName + ".bak")), "a finished job leaves no .bak to resurface");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
