// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System.Linq;
using System.Security.Cryptography;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Tests.Framework;
using Grog.Core.Verify;

public class VerifyImportTests
{
    static string Md5(string path)
    {
        using var s = File.OpenRead(path);
        using var md5 = MD5.Create();
        return System.Convert.ToHexString(md5.ComputeHash(s)).ToLowerInvariant();
    }

    static (JsonManifestStore store, string root) NewManifest()
    {
        var root = Directory.CreateTempSubdirectory("grog-verify-").FullName;
        return (new JsonManifestStore(root), root);
    }

    [Test]
    async Task Verify_FastMode_FlagsMissingAndSizeOk()
    {
        var (store, root) = NewManifest();
        try
        {
            await store.LoadAsync();
            var game = new LibraryItem { GogId = 1, Title = "Game" };
            // present file
            var presentPath = Path.Combine(root, "game", "setup.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(presentPath)!);
            await File.WriteAllTextAsync(presentPath, "hello world");
            game.Files.Add(new GameFile
            {
                GameGogId = 1, FileKey = "/dl/0", Name = "setup.exe",
                ExpectedSizeBytes = new FileInfo(presentPath).Length,
                LocalRelativePath = Path.GetRelativePath(root, presentPath),
                State = FileState.Present,
            });
            // missing file
            game.Files.Add(new GameFile
            {
                GameGogId = 1, FileKey = "/dl/1", Name = "missing.bin",
                ExpectedSizeBytes = 999,
                LocalRelativePath = Path.Combine("game", "missing.bin"),
                State = FileState.Present,
            });
            store.Current.Items.Add(game);
            await store.SaveAsync();

            var r = await new VerifyService(store, root).RunAsync(full: false);
            Assert.Equal(1, r.SizeOnlyOk, "one size-ok");
            Assert.Equal(1, r.Missing, "one missing");
            Assert.Equal(FileState.Missing, game.Files[1].State, "missing flagged");
        }
        finally { Directory.Delete(root, true); }
    }

    // Fix Now re-queues a gone file as NotBackedUp but keeps its old LocalRelativePath (the download
    // rewrites it). Verify must SKIP such files: re-flagging them from the leftover path is how a fixed
    // "gone from disk" error resurrected on every restart (owner-reported, 2026-08-31).
    [Test]
    async Task Verify_SkipsNotBackedUpFiles_EvenWithLeftoverLocalPath()
    {
        var (store, root) = NewManifest();
        try
        {
            await store.LoadAsync();
            var game = new LibraryItem { GogId = 1, Title = "Game" };
            game.Files.Add(new GameFile
            {
                GameGogId = 1, FileKey = "/dl/0", Name = "gone.bin",
                ExpectedSizeBytes = 999,
                LocalRelativePath = Path.Combine("game", "gone.bin"),   // absent on disk
                State = FileState.NotBackedUp,                          // queued for re-fetch by Fix Now
            });
            store.Current.Items.Add(game);
            await store.SaveAsync();

            var r = await new VerifyService(store, root).RunAsync(full: false);
            Assert.Equal(FileState.NotBackedUp, game.Files[0].State, "queued re-fetch stays NotBackedUp");
            Assert.Equal(0, r.Missing, "not re-flagged as missing");
        }
        finally { Directory.Delete(root, true); }
    }

    // A size-only pass must NEVER launder a Corrupt file back to good -- only a full MD5 match may clear it.
    [Test]
    async Task Verify_SizeOnly_DoesNotHealCorruptFile()
    {
        var (store, root) = NewManifest();
        try
        {
            await store.LoadAsync();
            var game = new LibraryItem { GogId = 1, Title = "Game" };
            var p = Path.Combine(root, "game", "setup.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            await File.WriteAllTextAsync(p, "hello world");
            // Already flagged Corrupt (a prior hash failure), but the on-disk size matches what we recorded.
            game.Files.Add(new GameFile
            {
                GameGogId = 1, FileKey = "/dl/0", Name = "setup.exe",
                LocalSizeBytes = new FileInfo(p).Length,
                LocalRelativePath = Path.GetRelativePath(root, p),
                State = FileState.Corrupt,
            });
            store.Current.Items.Add(game);
            await store.SaveAsync();

            var r = await new VerifyService(store, root).RunAsync(full: false);
            Assert.Equal(FileState.Corrupt, game.Files[0].State, "size-only cannot clear a corruption flag");
            Assert.Equal(1, r.Corrupt, "counted as corrupt, not size-ok");
        }
        finally { Directory.Delete(root, true); }
    }

    // Hash mismatch + GOG now ships a DIFFERENT checksum = silent repack (new build, no version bump) →
    // UpdateAvailable, not Corrupt. Same setup but GOG's current hash still equals ours → real corruption.
    [Test]
    async Task Verify_HashMismatch_SilentRepack_IsUpdateNotCorrupt()
    {
        var (store, root) = NewManifest();
        try
        {
            await store.LoadAsync();
            var game = new LibraryItem { GogId = 1, Title = "Game" };
            var p = Path.Combine(root, "game", "setup.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            await File.WriteAllTextAsync(p, "abc");   // md5 900150983cd24fb0d6963f7d28e17f72
            game.Files.Add(new GameFile
            {
                GameGogId = 1, FileKey = "/dl/0", Name = "setup.exe",
                LocalSizeBytes = new FileInfo(p).Length, LocalRelativePath = Path.GetRelativePath(root, p),
                ExpectedMd5 = "00000000000000000000000000000000",   // stored hash the disk won't match
                State = FileState.Present,
            });
            store.Current.Items.Add(game);
            await store.SaveAsync();

            // GOG now advertises a hash different from our stored one -> the build changed under us.
            var vs = new VerifyService(store, root)
            { FetchCurrentServerMd5 = (_, _) => System.Threading.Tasks.Task.FromResult<string?>("ffffffffffffffffffffffffffffffff") };
            var r = await vs.RunAsync(Grog.Core.Verify.VerifyMode.FullRehash);
            Assert.Equal(FileState.Outdated, game.Files[0].State, "different server hash -> update, not corruption");
            Assert.Equal(1, r.Updated, "counted as updated");
            Assert.Equal(0, r.Corrupt, "not counted corrupt");

            // Now GOG still ships OUR stored hash but the disk differs -> genuine corruption.
            game.Files[0].State = FileState.Present;
            var vs2 = new VerifyService(store, root)
            { FetchCurrentServerMd5 = (_, _) => System.Threading.Tasks.Task.FromResult<string?>("00000000000000000000000000000000") };
            var r2 = await vs2.RunAsync(Grog.Core.Verify.VerifyMode.FullRehash);
            Assert.Equal(FileState.Corrupt, game.Files[0].State, "server still ships our hash + disk differs -> corrupt");
            Assert.Equal(1, r2.Corrupt, "counted corrupt");
        }
        finally { Directory.Delete(root, true); }
    }

    // (09-25) An adopted file with no checksum on record: with FetchMissingChecksums the pass asks GOG, hashes, and
    // a match reads Verified with the checksum stored; a mismatch changes nothing and is counted (the user decides);
    // without the switch (or in SizeOnly) it stays a size check and GOG is never asked.
    private static async System.Threading.Tasks.Task<(JsonManifestStore store, string root, GameFile f)> AdoptedNoChecksum()
    {
        var (store, root) = NewManifest();
        await store.LoadAsync();
        var game = new LibraryItem { GogId = 1, Title = "Game" };
        var p = Path.Combine(root, "game", "setup.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        await File.WriteAllTextAsync(p, "abc");   // md5 900150983cd24fb0d6963f7d28e17f72
        var f = new GameFile
        {
            GameGogId = 1, FileKey = "/dl/0", Name = "setup.exe",
            LocalSizeBytes = new FileInfo(p).Length, LocalRelativePath = Path.GetRelativePath(root, p),
            State = FileState.Present,
        };
        game.Files.Add(f);
        store.Current.Items.Add(game);
        await store.SaveAsync();
        return (store, root, f);
    }

    [Test]
    async Task Verify_MissingChecksum_Fetched_Match_IsVerifiedAndStored()
    {
        var (store, root, f) = await AdoptedNoChecksum();
        try
        {
            var vs = new VerifyService(store, root)
            {
                FetchMissingChecksums = true,
                FetchCurrentServerMd5 = (_, _) => System.Threading.Tasks.Task.FromResult<string?>("900150983cd24fb0d6963f7d28e17f72"),
            };
            var r = await vs.RunAsync(Grog.Core.Verify.VerifyMode.Unverified);
            Assert.Equal(FileState.Verified, f.State, "GOG's checksum matches the bytes");
            Assert.Equal("900150983cd24fb0d6963f7d28e17f72", f.ExpectedMd5, "checksum stored on a match");
            Assert.Equal(1, r.Verified, "counted verified");
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    async Task Verify_MissingChecksum_Fetched_Mismatch_ChangesNothing_IsCounted()
    {
        var (store, root, f) = await AdoptedNoChecksum();
        try
        {
            var vs = new VerifyService(store, root)
            {
                FetchMissingChecksums = true,
                FetchCurrentServerMd5 = (_, _) => System.Threading.Tasks.Task.FromResult<string?>("ffffffffffffffffffffffffffffffff"),
            };
            var r = await vs.RunAsync(Grog.Core.Verify.VerifyMode.Unverified);
            Assert.Equal(FileState.Present, f.State, "record unchanged: never Outdated or Corrupt (nothing auto-queued)");
            Assert.True(string.IsNullOrEmpty(f.ExpectedMd5), "no checksum stored on a mismatch");
            Assert.Equal(1, r.DiffersFromGog, "counted as differing");
            Assert.Equal(0, r.Updated + r.Corrupt, "neither updated nor corrupt");
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    async Task Verify_MissingChecksum_CorruptStaysCorrupt()
    {
        var (store, root, f) = await AdoptedNoChecksum();
        try
        {
            f.State = FileState.Corrupt;
            var vs = new VerifyService(store, root)
            {
                FetchMissingChecksums = true,
                FetchCurrentServerMd5 = (_, _) => System.Threading.Tasks.Task.FromResult<string?>("ffffffffffffffffffffffffffffffff"),
            };
            var r = await vs.RunAsync(Grog.Core.Verify.VerifyMode.Unverified);
            Assert.Equal(FileState.Corrupt, f.State, "a mismatch never heals Corrupt");
            Assert.Equal(1, r.Corrupt, "counted corrupt");
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    async Task Verify_MissingChecksum_NotAsked_WhenSwitchOffOrSizeOnly()
    {
        var (store, root, f) = await AdoptedNoChecksum();
        try
        {
            int asked = 0;
            System.Func<GameFile, System.Threading.CancellationToken, System.Threading.Tasks.Task<string?>> fetch =
                (_, _) => { asked++; return System.Threading.Tasks.Task.FromResult<string?>("900150983cd24fb0d6963f7d28e17f72"); };
            var r1 = await new VerifyService(store, root) { FetchCurrentServerMd5 = fetch }.RunAsync(Grog.Core.Verify.VerifyMode.Unverified);
            var r2 = await new VerifyService(store, root) { FetchCurrentServerMd5 = fetch, FetchMissingChecksums = true }.RunAsync(Grog.Core.Verify.VerifyMode.SizeOnly);
            Assert.Equal(0, asked, "GOG never asked");
            Assert.Equal(FileState.Present, f.State, "size check only");
            Assert.Equal(1, r1.SizeOnlyOk, "switch off: size-only");
            Assert.Equal(1, r2.SizeOnlyOk, "SizeOnly: size-only");
        }
        finally { Directory.Delete(root, true); }
    }

    // A size mismatch flags Corrupt and must NOT overwrite the recorded good size with the bad on-disk size
    // (doing so is what let a later pass see "size matches" and self-heal).
    [Test]
    async Task Verify_SizeMismatch_KeepsRecordedGoodSize()
    {
        var (store, root) = NewManifest();
        try
        {
            await store.LoadAsync();
            var game = new LibraryItem { GogId = 1, Title = "Game" };
            var p = Path.Combine(root, "game", "setup.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            await File.WriteAllTextAsync(p, "short");   // 5 bytes on disk
            game.Files.Add(new GameFile
            {
                GameGogId = 1, FileKey = "/dl/0", Name = "setup.exe",
                LocalSizeBytes = 5000,                  // recorded good size, far larger than disk
                LocalRelativePath = Path.GetRelativePath(root, p),
                State = FileState.Present,
            });
            store.Current.Items.Add(game);
            await store.SaveAsync();

            await new VerifyService(store, root).RunAsync(full: false);
            Assert.Equal(FileState.Corrupt, game.Files[0].State, "size mismatch -> corrupt");
            Assert.Equal(5000L, game.Files[0].LocalSizeBytes ?? 0, "recorded good size preserved, not clobbered by the bad on-disk size");
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    async Task Verify_FastMode_RoundedEstimate_DoesNotFalseFlagCorrupt()
    {
        // Regression: ExpectedSizeBytes is a rounded estimate from GOG's "63.8 MB" text and won't
        // equal the exact byte count. Fast verify must trust the exact LocalSizeBytes recorded at
        // download time and NOT flag corrupt on the estimate mismatch.
        var (store, root) = NewManifest();
        try
        {
            await store.LoadAsync();
            var game = new LibraryItem { GogId = 3, Title = "Estimated" };
            var p = Path.Combine(root, "est", "setup.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            var payload = new string('x', 66_900_000); // ~63.8 MB actual
            await File.WriteAllTextAsync(p, payload);
            var actual = new FileInfo(p).Length;

            game.Files.Add(new GameFile
            {
                GameGogId = 3, FileKey = "/dl/est", Name = "setup.exe",
                ExpectedSizeBytes = 63_800_000,   // rounded estimate - deliberately != actual
                LocalSizeBytes = actual,          // exact, recorded at download
                LocalRelativePath = Path.GetRelativePath(root, p),
                State = FileState.Present,
            });
            store.Current.Items.Add(game);
            await store.SaveAsync();

            var r = await new VerifyService(store, root).RunAsync(full: false);
            Assert.Equal(0, r.Corrupt, "not flagged corrupt despite estimate mismatch");
            Assert.Equal(1, r.SizeOnlyOk, "counted as size-ok via exact LocalSizeBytes");
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    async Task Verify_FullMode_DetectsCorruptViaMd5()
    {
        var (store, root) = NewManifest();
        try
        {
            await store.LoadAsync();
            var game = new LibraryItem { GogId = 2, Title = "Game2" };
            var p = Path.Combine(root, "game2", "data.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            await File.WriteAllTextAsync(p, "correct content");
            var goodMd5 = Md5(p);
            // Now record a WRONG md5 to simulate corruption.
            game.Files.Add(new GameFile
            {
                GameGogId = 2, FileKey = "/dl/x", Name = "data.bin",
                ExpectedSizeBytes = new FileInfo(p).Length,
                ExpectedMd5 = new string('0', 32),
                LocalRelativePath = Path.GetRelativePath(root, p),
                State = FileState.Present,
            });
            store.Current.Items.Add(game);
            await store.SaveAsync();

            var r = await new VerifyService(store, root).RunAsync(full: true);
            Assert.Equal(1, r.Corrupt, "md5 mismatch -> corrupt");
            Assert.Equal(FileState.Corrupt, game.Files[0].State, "flagged corrupt");
        }
        finally { Directory.Delete(root, true); }
    }

    // (09-19, walk pass 3) The Storage FILE row's "Rescan & verify": a Verified file overwritten with same-size
    // junk passed the size-only file pass. Asked for a full re-hash, the file pass reads the bytes; asked for
    // nothing (every other single-file site), it stays the size-only confirm.
    [Test]
    async Task FilePass_FullRehash_CatchesSameSizeJunk_AndTheDefaultStaysSizeOnly()
    {
        var (store, root) = NewManifest();
        try
        {
            await store.LoadAsync();
            var game = new LibraryItem { GogId = 3, Title = "Game3" };
            var p = Path.Combine(root, "game3", "setup.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            await File.WriteAllTextAsync(p, "same size junk!");
            game.Files.Add(new GameFile
            {
                GameGogId = 3, FileKey = "/dl/s", Name = "setup.exe",
                ExpectedSizeBytes = new FileInfo(p).Length, LocalSizeBytes = new FileInfo(p).Length,
                ExpectedMd5 = new string('0', 32),
                LocalRelativePath = Path.GetRelativePath(root, p),
                State = FileState.Verified,
            });
            store.Current.Items.Add(game);
            await store.SaveAsync();
            var keys = new[] { (3L, "/dl/s") };

            var confirm = await Grog.Core.Runs.VerifyRun.RunAsync(store, root, Grog.Core.Runs.VerifyRequest.ForFiles(keys),
                new Grog.Core.Runs.NullBackupHost(), CancellationToken.None);
            Assert.Equal(0, confirm.Verify.Corrupt, "the default file pass is a size-only confirm");

            var full = await Grog.Core.Runs.VerifyRun.RunAsync(store, root, Grog.Core.Runs.VerifyRequest.ForFiles(keys, VerifyMode.FullRehash),
                new Grog.Core.Runs.NullBackupHost(), CancellationToken.None);
            Assert.Equal(1, full.Verify.Corrupt, "a full re-hash reads the bytes");
            Assert.Equal(FileState.Corrupt, game.Files[0].State, "flagged corrupt");
        }
        finally { Directory.Delete(root, true); }
    }
}
