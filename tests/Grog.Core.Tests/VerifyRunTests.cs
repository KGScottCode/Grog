// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Runs;
using Grog.Core.Storage;
using Grog.Core.Tests.Framework;
using Grog.Core.Verify;
using Grog.Core.Volumes;

/// <summary>The one verify sequence (S2.1, 09-08) that replaces the App's six VerifyService sites and the CLI's.</summary>
[NewBatch]
[Trait("run")]
public class VerifyRunTests
{
    private sealed class RecordingHost : NullBackupHost
    {
        public List<(string Message, bool IsError)> Logged { get; } = new();
        public override void Log(string message, bool isError = false) => Logged.Add((message, isError));
    }

    private sealed class Rig : IDisposable
    {
        public readonly string Dir, Root;
        public readonly JsonManifestStore Store;
        public readonly RecordingHost Host = new();
        public Rig()
        {
            Dir = Directory.CreateTempSubdirectory("grog-verifyrun-").FullName;
            Root = Path.Combine(Dir, "backup");
            Directory.CreateDirectory(Root);
            Environment.SetEnvironmentVariable("GROG_CONFIG_DIR", Path.Combine(Dir, "_cfg"));
            Store = new JsonManifestStore(GrogPaths.Resolve(Dir));
            Store.LoadAsync().GetAwaiter().GetResult();
            BackupLayout.EnsurePrimary(Store.Current, Root);
        }

        /// <summary>A product with one file recorded as Present; <paramref name="onDisk"/> writes its bytes.</summary>
        public LibraryItem Item(long id, bool onDisk, long size = 16)
        {
            var it = new LibraryItem { GogId = id, Title = $"Game {id}", Slug = $"game_{id}" };
            var rel = Path.Combine("Games", $"game_{id}", $"setup_{id}.exe");
            var f = new GameFile
            {
                GameGogId = id, FileKey = $"/downlink/game_{id}/en1installer0", Name = "Setup", Kind = FileKind.Installer,
                State = FileState.Present, LocalRelativePath = rel, LocalSizeBytes = size, RootId = Store.Current.PrimaryRootId,
            };
            it.Files.Add(f);
            if (onDisk)
            {
                var path = Path.Combine(Root, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, new byte[size]);
            }
            Store.Current.Items.Add(it);
            return it;
        }

        public JsonManifestStore Reload()
        {
            var again = new JsonManifestStore(GrogPaths.Resolve(Dir));
            again.LoadAsync().GetAwaiter().GetResult();
            return again;
        }

        public void Dispose() { try { Directory.Delete(Dir, true); } catch { } }
    }

    [Test]
    async Task WholeLibrary_SizeOnly_MarksMissing_RollsUp_AndSaves()
    {
        using var rig = new Rig();
        var present = rig.Item(1, onDisk: true);
        var gone = rig.Item(2, onDisk: false);
        present.Status = BackupStatus.Unknown; gone.Status = BackupStatus.Unknown;

        var r = await VerifyRun.RunAsync(rig.Store, rig.Root, VerifyRequest.LibrarySizeOnly(), rig.Host, CancellationToken.None);

        Assert.Equal(VerifyScope.Library, r.Scope, "no ids = whole library");
        Assert.Equal(VerifyMode.SizeOnly, r.Mode, "size mode");
        Assert.Equal(2, r.Verify.Checked, "both files checked");
        Assert.Equal(1, r.Verify.Missing, "the one not on disk is missing");
        Assert.Equal(1, r.Verify.SizeOnlyOk, "the one on disk is size-ok");
        Assert.True(r.AnyProblems, "a missing file is a problem");
        Assert.Equal(FileState.Missing, gone.Files[0].State, "state written");
        Assert.Equal(FileState.Present, present.Files[0].State, "present stays present (size-only never claims Verified)");

        // The post-pass: statuses rolled up for every product and the manifest saved.
        Assert.NotEqual(BackupStatus.Unknown, present.Status, "RecomputeStatus ran on the intact product");
        Assert.NotEqual(BackupStatus.Unknown, gone.Status, "RecomputeStatus ran on the damaged product");
        var again = rig.Reload();
        Assert.Equal(FileState.Missing, again.Current.ItemById(2)!.Files[0].State, "the verdict was persisted");

        Assert.Equal(1, rig.Host.Logged.Count, "one summary line for the host");
        Assert.True(rig.Host.Logged[0].IsError, "flagged as a problem");
        Assert.Contains("1 missing", rig.Host.Logged[0].Message, "summary names the count");
    }

    [Test]
    async Task Items_Scope_TouchesOnlyTheNamedProducts()
    {
        using var rig = new Rig();
        var a = rig.Item(1, onDisk: false);
        var b = rig.Item(2, onDisk: false);

        var r = await VerifyRun.RunAsync(rig.Store, rig.Root, VerifyRequest.ConfirmItems(new[] { 1L }), rig.Host, CancellationToken.None);

        Assert.Equal(VerifyScope.Items, r.Scope, "item ids = items scope");
        Assert.Equal(1, r.Items, "one product resolved");
        Assert.Equal(1, r.Verify.Missing, "only its file was checked");
        Assert.Equal(FileState.Missing, a.Files[0].State, "named product re-checked");
        Assert.Equal(FileState.Present, b.Files[0].State, "the other product untouched");
    }

    [Test]
    async Task Files_Scope_IsAConfirm_AndResolvesKeysToParents()
    {
        using var rig = new Rig();
        var a = rig.Item(1, onDisk: true);
        rig.Item(2, onDisk: false);
        var key = (a.GogId, a.Files[0].FileKey);

        var r = await VerifyRun.RunAsync(rig.Store, rig.Root, VerifyRequest.ForFiles(new[] { key }, VerifyMode.Unverified), rig.Host, CancellationToken.None);

        Assert.Equal(VerifyScope.Files, r.Scope, "keys = files scope");
        Assert.Equal(VerifyMode.SizeOnly, r.Mode, "a file pass is a size-only confirm unless a FULL re-hash was asked (09-19; that case: VerifyImportTests)");
        Assert.Equal(1, r.Files, "one file resolved");
        Assert.Equal(1, r.Items, "one parent");
        Assert.Equal(0, r.Verify.Missing, "the other product's gone file was not examined");
        Assert.Equal(1, r.Verify.SizeOnlyOk, "the named file is present");
    }

    [Test]
    async Task Full_LiftsTheModeToRehash_AndProgressFires()
    {
        using var rig = new Rig();
        rig.Item(1, onDisk: true);
        var ticks = new List<VerifyProgress>();
        var req = VerifyRequest.LibraryRescan(full: true) with { Progress = p => ticks.Add(p) };
        Assert.Equal(VerifyMode.FullRehash, req.EffectiveMode, "Full wins over the asked mode");

        var r = await VerifyRun.RunAsync(rig.Store, rig.Root, req, rig.Host, CancellationToken.None);
        Assert.Equal(VerifyMode.FullRehash, r.Mode, "ran in full mode");
        Assert.Equal(1, ticks.Count, "one progress tick per file");
        Assert.Equal((1, 1), (ticks[0].Done, ticks[0].Total), "done/total");
    }
}
