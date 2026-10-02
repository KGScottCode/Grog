// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System.IO;
using System.Threading.Tasks;
using Grog.Core.Download;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Tests.Framework;

/// <summary>The 1060-1083 display-name save is detected by "on-disk name == sanitized label", renamed in
/// place to the resolved real name, never overwrites an existing file, and converges to no candidates.</summary>
[NewBatch]
[Trait("download")]
public class MisnamedFileRepairTests
{
    private sealed class FlatLayout : Grog.Core.Volumes.IBackupLayout
    {
        private readonly string _root;
        public FlatLayout(string root) { _root = root; }
        public string? ResolvePath(GameFile f) => f.LocalRelativePath is { } r ? Path.Combine(_root, r) : null;
        public string? ResolveTargetDir(long gogId, string slug, FileKind kind, out string rootId, GameFile? f = null) { rootId = "primary"; return _root; }
        public string? RootPath(string? rootId) => _root;
        public System.Collections.Generic.IReadOnlyCollection<string> OnlineRootPaths => new[] { _root };
        public bool IsOnline(string? rootId) => true;
        public string SubPathFor(string slug, FileKind kind, GameFile? f = null, long gogId = 0, string? rootId = null) => "";
        public string? ResolveCloudDir(string slug, out string rootId) { rootId = "primary"; return _root; }
    }

    [Test]
    public async Task Display_named_file_is_renamed_to_its_real_name_and_manifest_follows()
    {
        var root = Path.Combine(Path.GetTempPath(), "grog-repair-" + System.Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(root, "Games", "vambrace"));
        File.WriteAllText(Path.Combine(root, "Games", "vambrace", "Vambrace_ Cold Soul (Part 1 of 2)"), "bytes");
        var m = new LibraryManifest();
        var item = new LibraryItem { GogId = 1, Title = "Vambrace: Cold Soul", Slug = "vambrace" };
        var f = new GameFile { GameGogId = 1, FileKey = "/k1", Name = "Vambrace: Cold Soul (Part 1 of 2)", Kind = FileKind.Installer,
                               State = FileState.Verified, LocalRelativePath = "Games/vambrace/Vambrace_ Cold Soul (Part 1 of 2)" };
        item.Files.Add(f);
        var ok = new GameFile { GameGogId = 1, FileKey = "/k2", Name = "manual (36 pages)", Kind = FileKind.Extra,
                                State = FileState.Verified, LocalRelativePath = "Games/vambrace/vambrace_manual.pdf" };
        item.Files.Add(ok);
        m.Items.Add(item);

        Assert.Equal(1, MisnamedFileRepair.FindCandidates(m).Count, "only the label-named file is a candidate");

        int n = await MisnamedFileRepair.RepairAsync(m, new FlatLayout(root),
            (file, ct) => Task.FromResult<string?>("setup_vambrace_1.0_(12345)-1.bin"), null);

        Assert.Equal(1, n);
        Assert.True(File.Exists(Path.Combine(root, "Games", "vambrace", "setup_vambrace_1.0_(12345)-1.bin")), "renamed on disk");
        Assert.False(File.Exists(Path.Combine(root, "Games", "vambrace", "Vambrace_ Cold Soul (Part 1 of 2)")), "old name gone");
        Assert.Equal("Games/vambrace/setup_vambrace_1.0_(12345)-1.bin", f.LocalRelativePath);
        Assert.Equal(0, MisnamedFileRepair.FindCandidates(m).Count, "converges");
        Directory.Delete(root, true);
    }

    [Test]
    public async Task Never_overwrites_an_existing_real_named_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "grog-repair-" + System.Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "avatars"), "label-named");
        File.WriteAllText(Path.Combine(root, "avatars.zip"), "already here");
        var m = new LibraryManifest();
        var item = new LibraryItem { GogId = 2, Title = "X", Slug = "x" };
        item.Files.Add(new GameFile { GameGogId = 2, FileKey = "/a", Name = "avatars", Kind = FileKind.Extra,
                                      State = FileState.Verified, LocalRelativePath = "avatars" });
        m.Items.Add(item);

        int n = await MisnamedFileRepair.RepairAsync(m, new FlatLayout(root), (f, ct) => Task.FromResult<string?>("avatars.zip"), null);

        Assert.Equal(0, n, "clash skipped");
        Assert.Equal("already here", File.ReadAllText(Path.Combine(root, "avatars.zip")), "existing file untouched");
        Assert.Equal("avatars", item.Files[0].LocalRelativePath, "manifest unchanged");
        Directory.Delete(root, true);
    }
}
