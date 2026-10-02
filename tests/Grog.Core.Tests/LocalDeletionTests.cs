// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Sync;
using Grog.Core.Tests.Framework;

namespace Grog.Core.Tests;

/// <summary>(09-11) The user's Delete. Real directories under temp, so the empty-folder rule and the
/// Old Versions rule are exercised against the file system, not a fake.</summary>
[Trait("deletion")]
public sealed class LocalDeletionTests
{
    private sealed class FlatLayout : Grog.Core.Volumes.IBackupLayout
    {
        private readonly string _root;
        public FlatLayout(string root) { _root = root; }
        public string? ResolvePath(GameFile f) => f.LocalRelativePath is { } r && f.RootId != "offline" ? Path.Combine(_root, r) : null;
        public string? ResolveTargetDir(long gogId, string slug, FileKind kind, out string rootId, GameFile? f = null) { rootId = "primary"; return _root; }
        public string? RootPath(string? rootId) => _root;
        public IReadOnlyCollection<string> OnlineRootPaths => new[] { _root };
        public bool IsOnline(string? rootId) => rootId != "offline";
        public string SubPathFor(string slug, FileKind kind, GameFile? f = null, long gogId = 0, string? rootId = null) => "";
        public string? ResolveCloudDir(string slug, out string rootId) { rootId = "primary"; return _root; }
    }

    private static string Root() { var r = Path.Combine(Path.GetTempPath(), "grog-del-" + System.Guid.NewGuid().ToString("N")[..8]); Directory.CreateDirectory(r); return r; }

    private static GameFile Put(string root, LibraryItem item, string key, string rel, string content = "bytes")
    {
        var p = Path.Combine(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        var f = new GameFile { GameGogId = item.GogId, FileKey = key, Name = Path.GetFileName(rel), Kind = FileKind.Installer,
                               State = FileState.Verified, RootId = "primary", LocalRelativePath = rel, LocalSizeBytes = content.Length };
        item.Files.Add(f);
        return f;
    }

    private static (LibraryManifest m, LibraryItem item) Lib()
    {
        var m = new LibraryManifest(); m.PrimaryRootId = "primary";
        var item = new LibraryItem { GogId = 1, Title = "One", Slug = "one" };
        m.Items.Add(item);
        return (m, item);
    }

    [Test]
    public void Deletes_the_bytes_and_clears_the_record()
    {
        var root = Root(); var (m, item) = Lib();
        var f = Put(root, item, "/a", "Games/one/setup.exe");
        f.DownloadedAt = DateTimeOffset.UtcNow; f.LastVerifiedAt = DateTimeOffset.UtcNow; f.FailedAttempts = 2;
        var r = LocalDeletion.Delete(m, new FlatLayout(root), new[] { f });
        Assert.Equal(1, r.Deleted, "one file"); Assert.Equal(5L, r.Bytes, "its bytes");
        Assert.False(File.Exists(Path.Combine(root, "Games/one/setup.exe")), "gone from disk");
        Assert.Equal(FileState.NotBackedUp, f.State, "back to not backed up");
        Assert.True(f.RootId is null && f.LocalRelativePath is null && f.LocalSizeBytes is null, "local facts cleared");
        Assert.True(f.DownloadedAt is null && f.LastVerifiedAt is null && f.FailedAttempts == 0, "history cleared: as if never touched");
    }

    // Sweep 2 #7: removing a drive with "Delete files" was a hand loop. One plan for the whole drive.
    [Test]
    public void A_whole_drive_delete_takes_queued_files_and_orphan_archives_and_leaves_other_drives_alone()
    {
        var root = Root(); var (m, item) = Lib();
        var here = Put(root, item, "/a", "Games/one/setup.exe");
        var queued = Put(root, item, "/q", "Games/one/patch.exe"); queued.State = FileState.Outdated;
        m.Downloads.Enqueue(1, "/q");
        var elsewhere = Put(root, item, "/e", "Games/one/other.bin"); elsewhere.RootId = "second";
        // A retained build on this drive whose CURRENT file lives on the other one.
        var oldPath = Path.Combine(root, "Old Versions/one/other_old.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(oldPath)!); File.WriteAllText(oldPath, "old");
        var orphan = new GameFile { GameGogId = 1, FileKey = "/e#old:other_old.bin", Name = "other.bin", IsOldVersion = true,
                                    RootId = "primary", LocalRelativePath = "Old Versions/one/other_old.bin", LocalSizeBytes = 3, State = FileState.Verified };
        item.OldVersionFiles.Add(orphan);

        var plan = LocalDeletion.PlanForRoot(m, new FlatLayout(root), "primary");
        var failures = new List<string>();
        int ticks = 0;
        var outcomes = LocalDeletion.Execute(plan, null, failures, (_, _, _) => ticks++);
        var r = LocalDeletion.Apply(m, plan, outcomes, failures);

        Assert.Equal(3, r.Deleted, "both files on the drive and the orphan archive; the queued one is not skipped");
        Assert.Equal(3, ticks, "one progress tick per target");
        Assert.True(here.LocalRelativePath is null && queued.LocalRelativePath is null, "records reset");
        Assert.True(m.Downloads.Contains(1, "/q"), "the queue entry stays: the download is still wanted");
        Assert.False(item.OldVersionFiles.Contains(orphan), "the archive record left with its bytes");
        Assert.False(File.Exists(oldPath), "and its bytes are gone");
        Assert.Equal("second", elsewhere.RootId, "a file on another drive is untouched");
        Assert.True(File.Exists(Path.Combine(root, "Games/one/other.bin")), "its bytes too");
    }

    [Test]
    public void Deleting_a_withdrawn_archive_record_removes_the_bytes_and_the_record()
    {
        var root = Root(); var (m, item) = Lib();
        var keep = Put(root, item, "/a", "Games/one/setup.exe");
        var withdrawn = Put(root, item, "/w", "Games/one/bonus.zip", "withdrawn");
        item.Files.Remove(withdrawn); withdrawn.IsOldVersion = true; withdrawn.WithdrawnByGog = true;
        item.OldVersionFiles.Add(withdrawn);
        Assert.True(BackupScope.IsWithdrawnOnDisk(withdrawn), "the Library's predicate reaches it");

        var r = LocalDeletion.Delete(m, new FlatLayout(root), new[] { withdrawn });

        Assert.Equal(1, r.Deleted, "one file"); Assert.Equal(9L, r.Bytes, "its bytes");
        Assert.False(File.Exists(Path.Combine(root, "Games/one/bonus.zip")), "gone from disk");
        Assert.False(item.OldVersionFiles.Contains(withdrawn), "the record left the archive list: nothing to go back to");
        Assert.True(File.Exists(Path.Combine(root, "Games/one/setup.exe")) && item.Files.Contains(keep), "the current file is untouched");
    }

    [Test]
    public void Removes_the_game_folder_once_it_is_empty_but_never_one_with_content()
    {
        var root = Root(); var (m, item) = Lib();
        var a = Put(root, item, "/a", "Games/one/setup.exe");
        var b = Put(root, item, "/b", "Games/one/setup-2.bin");
        LocalDeletion.Delete(m, new FlatLayout(root), new[] { a });
        Assert.True(Directory.Exists(Path.Combine(root, "Games/one")), "b is still there: folder stays");
        LocalDeletion.Delete(m, new FlatLayout(root), new[] { b });
        Assert.False(Directory.Exists(Path.Combine(root, "Games/one")), "last file out: folder goes");
        Assert.True(Directory.Exists(Path.Combine(root, "Games")), "only the game's own folder, not the library's");
    }

    [Test]
    public void Takes_the_files_archived_builds_under_Old_Versions_with_it()
    {
        var root = Root(); var (m, item) = Lib();
        var f = Put(root, item, "/a", "Games/one/setup.exe");
        var oldPath = Path.Combine(root, "Old Versions/one/setup_old.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(oldPath)!); File.WriteAllText(oldPath, "oldbuild");
        item.OldVersionFiles.Add(new GameFile { GameGogId = 1, FileKey = "/a#old:setup_old.exe", Name = "setup.exe", IsOldVersion = true,
                                                 RootId = "primary", LocalRelativePath = "Old Versions/one/setup_old.exe", LocalSizeBytes = 8, State = FileState.Verified });
        var other = new GameFile { GameGogId = 1, FileKey = "/zz#old:x", IsOldVersion = true, LocalRelativePath = "Old Versions/one/x" };
        item.OldVersionFiles.Add(other);

        var r = LocalDeletion.Delete(m, new FlatLayout(root), new[] { f });
        Assert.Equal(13L, r.Bytes, "file + its archived build");
        Assert.False(File.Exists(oldPath), "the archived build is gone");
        Assert.Equal(1, item.OldVersionFiles.Count, "another file's archive is untouched");
        Assert.True(ReferenceEquals(item.OldVersionFiles[0], other), "and it is the other one");
    }

    [Test]
    public void A_queued_file_is_skipped_not_raced()
    {
        var root = Root(); var (m, item) = Lib();
        var f = Put(root, item, "/a", "Games/one/setup.exe");
        m.Downloads.Enqueue(1, "/a", "primary");
        var r = LocalDeletion.Delete(m, new FlatLayout(root), new[] { f });
        Assert.Equal(0, r.Deleted, "nothing deleted"); Assert.Equal(1, r.Skipped, "reported as skipped");
        Assert.True(File.Exists(Path.Combine(root, "Games/one/setup.exe")), "bytes untouched");
        Assert.Equal(FileState.Verified, f.State, "record untouched");
    }

    [Test]
    public void A_file_already_missing_from_disk_still_has_its_record_cleared()
    {
        var root = Root(); var (m, item) = Lib();
        var f = Put(root, item, "/a", "Games/one/setup.exe");
        File.Delete(Path.Combine(root, "Games/one/setup.exe"));
        var r = LocalDeletion.Delete(m, new FlatLayout(root), new[] { f });
        Assert.Equal(1, r.Deleted, "counted: the user asked for it gone and it is");
        Assert.Equal(1, r.Missing, "and told it was already gone");
        Assert.Equal(FileState.NotBackedUp, f.State, "record cleared");
    }

    [Test]
    public void A_file_on_an_offline_drive_is_a_failure_and_keeps_its_record()
    {
        var root = Root(); var (m, item) = Lib();
        var f = Put(root, item, "/a", "Games/one/setup.exe"); f.RootId = "offline";
        var r = LocalDeletion.Delete(m, new FlatLayout(root), new[] { f });
        Assert.Equal(0, r.Deleted, "nothing deleted"); Assert.Equal(1, r.Failures.Count, "one failure, named");
        Assert.Equal(FileState.Verified, f.State, "record intact: the bytes are still on that drive");
    }

    [Test]
    public void A_root_folder_absent_from_disk_is_an_unplugged_drive_not_a_missing_file()
    {
        // The primary binds to the caller's path even when that path does not exist, so ResolvePath returns a
        // path on a drive that is not here. Every file on it would read "missing" and lose its record while
        // the bytes and Old Versions sit untouched on the unplugged drive.
        var root = Root(); var (m, item) = Lib();
        var f = Put(root, item, "/a", "Games/one/setup.exe");
        Directory.Delete(root, recursive: true);
        var r = LocalDeletion.Delete(m, new FlatLayout(root), new[] { f });
        Assert.Equal(0, r.Deleted, "nothing deleted"); Assert.Equal(0, r.Missing, "not counted missing");
        Assert.Equal(1, r.Failures.Count, "named as a disconnected drive");
        Assert.Equal(FileState.Verified, f.State, "record intact");
        Assert.True(f.LocalRelativePath is not null, "path kept");
    }

    /// <summary>(09-13) One pruning rule with the reorganize: the top-level Games/ folder is never removed, even
    /// when a file imported directly under it was the last thing in it.</summary>
    [Test]
    public void The_top_level_category_folder_survives_the_last_delete()
    {
        var root = Root(); var (m, item) = Lib();
        var f = Put(root, item, "/a", "Games/loose.exe");
        LocalDeletion.Delete(m, new FlatLayout(root), new[] { f });
        Assert.False(File.Exists(Path.Combine(root, "Games/loose.exe")), "file gone");
        Assert.True(Directory.Exists(Path.Combine(root, "Games")), "Games/ is the library's, not the file's");
    }

    /// <summary>(09-13) The resumable .part goes with the file it belonged to; before, HasPartial was cleared and
    /// the .part sat in .grog-tmp as an orphan.</summary>
    [Test]
    public void The_files_partial_goes_with_it()
    {
        var root = Root(); var (m, item) = Lib();
        var f = Put(root, item, "/a", "Games/one/setup.exe"); f.HasPartial = true; f.PartialBytes = 3;
        var part = Grog.Core.Download.DownloadEngine.PartialPathFor(root, f);
        Directory.CreateDirectory(Path.GetDirectoryName(part)!); File.WriteAllText(part, "abc");
        LocalDeletion.Delete(m, new FlatLayout(root), new[] { f });
        Assert.False(File.Exists(part), "the .part is gone too");
        Assert.False(f.HasPartial, "and the record agrees");
    }

    [Test]
    public void Files_with_no_local_copy_are_ignored()
    {
        var root = Root(); var (m, item) = Lib();
        var f = new GameFile { GameGogId = 1, FileKey = "/n", Name = "n", State = FileState.NotBackedUp }; item.Files.Add(f);
        var r = LocalDeletion.Delete(m, new FlatLayout(root), new[] { f });
        Assert.Equal(0, r.Deleted, "nothing to delete"); Assert.Equal(0, r.Failures.Count, "nothing to fail");
    }

    [Test]
    public void An_archived_build_on_an_offline_drive_is_a_named_failure_and_not_previewed()
    {
        var root = Root(); var (m, item) = Lib();
        var f = Put(root, item, "/a", "Games/one/setup.exe");
        var away = new GameFile { GameGogId = 1, FileKey = "/a#old:setup_old.exe", Name = "setup.exe", IsOldVersion = true,
                                  RootId = "offline", LocalRelativePath = "Old Versions/one/setup_old.exe", LocalSizeBytes = 8, State = FileState.Verified };
        item.OldVersionFiles.Add(away);
        var layout = new FlatLayout(root);

        var (_, bytes, _) = LocalDeletion.Preview(m, new[] { f }, layout);
        Assert.Equal(5L, bytes, "the archive on the unplugged drive is not priced as freed");

        var plan = LocalDeletion.Plan(m, layout, new[] { f });
        Assert.Equal(1, plan.Failures.Count, "the archive is a named failure");
        Assert.Contains("its drive is not connected", plan.Failures[0], "the same wording as a file on an unplugged drive");
        Assert.Equal(0, plan.Targets[0].OldVersions.Count, "and is not a target");
    }

    [Test]
    public void Preview_counts_what_delete_will_do()
    {
        var root = Root(); var (m, item) = Lib();
        var a = Put(root, item, "/a", "Games/one/setup.exe");
        var b = Put(root, item, "/b", "Games/one/two.bin", "xx");
        var n = new GameFile { GameGogId = 1, FileKey = "/n", Name = "n", State = FileState.NotBackedUp }; item.Files.Add(n);
        m.Downloads.Enqueue(1, "/b", "primary");
        var (files, bytes, queued) = LocalDeletion.Preview(m, new[] { a, b, n });
        Assert.Equal(1, files, "a only"); Assert.Equal(5L, bytes, "a's bytes"); Assert.Equal(1, queued, "b is queued");
    }
}
