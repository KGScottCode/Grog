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
using Grog.Core.Sync;
using Grog.Core.Tests.Framework;
using Grog.Core.Volumes;

/// <summary>
/// The portable hint, a withdrawn GOG slot, the UNC probe, and the saved-scope writer (09-28). The .grog-root marker
/// and its relocation search left in 1284: a storage that comes back under another letter is matched by its files
/// when the folder is added (ImportRun.MatchExistingRoot), not by a file Grog leaves in the backup folder.
/// </summary>
[Trait("volumes")]
public sealed class VolumesPass0928Tests
{
    private sealed class MemoryStore : IManifestStore
    {
        public LibraryManifest Current { get; } = new();
        public ManifestGate Gate { get; } = new();
        public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private static string Temp(string tag)
    {
        var d = Path.Combine(Path.GetTempPath(), "grog-" + tag + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    private static void Nuke(string dir) { try { Directory.Delete(dir, recursive: true); } catch { } }

    // ---- F2: marker -------------------------------------------------------------------------------------------

    // ---- F3: portable hint ------------------------------------------------------------------------------------

    [Test]
    void A_secondary_under_the_portable_install_re_binds_after_the_install_moves()
    {
        var b = Temp("port");
        var envBefore = Environment.GetEnvironmentVariable("GROG_CONFIG_DIR");
        try
        {
            Environment.SetEnvironmentVariable("GROG_CONFIG_DIR", null);
            GrogPaths.ProfileDirOverride = Path.Combine(b, "_profile");
            var install = Path.Combine(b, "install");
            Directory.CreateDirectory(Path.Combine(install, "GrogData"));
            GrogPaths.ExeDirOverride = install;
            GrogPaths.ForgetPortableProbe();
            Assert.NotNull(GrogPaths.PortableInstallDir(), "portable mode is on for this test");

            var store = new MemoryStore();
            var primary = Path.Combine(install, "Library"); Directory.CreateDirectory(primary);
            _ = new BackupLayout(store.Current, primary);
            var vol = new VolumeService(store);
            var second = vol.AddRoot(Path.Combine(install, "Library2"), "Second");
            _ = new BackupLayout(store.Current, primary);   // binds the secondary: the token is stamped here
            Assert.True(second.PortablePath is { } tok && tok.StartsWith(GrogPaths.PortableToken), "hint under the install: kept as a token too");

            // The stick re-letters: the whole install is somewhere else, the absolute hints are dead.
            var moved = Path.Combine(b, "mounted-elsewhere");
            Directory.Move(install, moved);
            GrogPaths.ExeDirOverride = moved;
            GrogPaths.ForgetPortableProbe();
            var layout = new BackupLayout(store.Current, Path.Combine(moved, "Library"));

            Assert.True(layout.IsOnline(second.Id), "the secondary re-binds through its token");
            Assert.Equal(Path.GetFullPath(Path.Combine(moved, "Library2")), second.PathHint, "and the hint heals to the live path");
        }
        finally
        {
            GrogPaths.ExeDirOverride = null; GrogPaths.ProfileDirOverride = null; GrogPaths.ForgetPortableProbe();
            Environment.SetEnvironmentVariable("GROG_CONFIG_DIR", envBefore);
            Nuke(b);
        }
    }

    [Test]
    void A_read_only_GrogData_sets_the_fallback_reason()
    {
        if (OperatingSystem.IsWindows()) { Assert.SkipUnless(false, "unix permission test"); return; }
        var b = Temp("portro");
        var envBefore = Environment.GetEnvironmentVariable("GROG_CONFIG_DIR");
        var data = Path.Combine(b, "install", "GrogData");
        try
        {
            Environment.SetEnvironmentVariable("GROG_CONFIG_DIR", null);
            GrogPaths.ProfileDirOverride = Path.Combine(b, "_profile");
            Directory.CreateDirectory(data);
            GrogPaths.ExeDirOverride = Path.Combine(b, "install");
            GrogPaths.ForgetPortableProbe();
            Assert.Null(GrogPaths.PortableFallbackReason, "writable: no reason");
            File.SetUnixFileMode(data, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            GrogPaths.ForgetPortableProbe();
            var dir = GrogPaths.ResolveConfigDir();
            Assert.SkipUnless(dir != data, "running as root: everything is writable");
            Assert.True(GrogPaths.PortableFallbackReason is { Length: > 0 } r && r.Contains(data), "the reason names the folder that was skipped");
        }
        finally
        {
            try { File.SetUnixFileMode(data, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); } catch { }
            GrogPaths.ExeDirOverride = null; GrogPaths.ProfileDirOverride = null; GrogPaths.ForgetPortableProbe();
            Environment.SetEnvironmentVariable("GROG_CONFIG_DIR", envBefore);
            Nuke(b);
        }
    }

    // ---- F4: a slot GOG withdrew --------------------------------------------------------------------------------

    private static LibraryItem Game(params (string key, FileState state, bool bytes)[] files)
    {
        var it = new LibraryItem { GogId = 7, Title = "Venba", Slug = "venba" };
        foreach (var (key, state, bytes) in files)
            it.Files.Add(new GameFile
            {
                GameGogId = 7, FileKey = key, Kind = FileKind.Installer, Name = key, State = state,
                LocalRelativePath = bytes ? $"Games/venba/{key}" : null, ExpectedSizeBytes = 100,
            });
        return it;
    }

    [Test]
    void A_withdrawn_slot_we_hold_bytes_for_moves_to_the_archive_list_flagged()
    {
        var existing = Game(("setup.exe", FileState.Verified, true), ("bonus.zip", FileState.Verified, true));
        var incoming = Game(("setup.exe", FileState.NotBackedUp, false));

        LibrarySyncService.MergeIntoForTests(existing, incoming);

        Assert.Equal(1, existing.Files.Count, "the withdrawn slot left the current list");
        var kept = existing.OldVersionFiles.Single();
        Assert.Equal("bonus.zip", kept.FileKey, "its record is in the archive list");
        Assert.True(kept.WithdrawnByGog && kept.IsOldVersion, "flagged as withdrawn, an archive record");
        Assert.Equal("Games/venba/bonus.zip", kept.LocalRelativePath, "the bytes stay where they are");
        Assert.Equal(FileState.Verified, kept.State, "no state invented");
    }

    [Test]
    void A_withdrawn_slot_without_bytes_is_just_dropped()
    {
        var existing = Game(("setup.exe", FileState.Verified, true), ("bonus.zip", FileState.NotBackedUp, false));
        LibrarySyncService.MergeIntoForTests(existing, Game(("setup.exe", FileState.NotBackedUp, false)));
        Assert.Equal(1, existing.Files.Count, "gone from the current list");
        Assert.Equal(0, existing.OldVersionFiles.Count, "nothing to keep: no bytes were held");
    }

    [Test]
    void A_withdrawn_slot_is_recorded_once_across_scans()
    {
        var existing = Game(("setup.exe", FileState.Verified, true), ("bonus.zip", FileState.Verified, true));
        LibrarySyncService.MergeIntoForTests(existing, Game(("setup.exe", FileState.NotBackedUp, false)));
        LibrarySyncService.MergeIntoForTests(existing, Game(("setup.exe", FileState.NotBackedUp, false)));
        Assert.Equal(1, existing.OldVersionFiles.Count, "one record, however many scans");
    }

    [Test]
    void A_withdrawn_slot_GOG_relists_is_reinstated_not_duplicated()
    {
        var existing = Game(("setup.exe", FileState.Verified, true), ("bonus.zip", FileState.Verified, true));
        LibrarySyncService.MergeIntoForTests(existing, Game(("setup.exe", FileState.NotBackedUp, false)));
        LibrarySyncService.MergeIntoForTests(existing, Game(("setup.exe", FileState.NotBackedUp, false), ("bonus.zip", FileState.NotBackedUp, false)));

        Assert.Equal(2, existing.Files.Count, "the slot is back in the current list");
        Assert.Equal(0, existing.OldVersionFiles.Count, "and gone from the archive list: one record, not two");
        var back = existing.Files.Single(f => f.FileKey == "bonus.zip");
        Assert.Equal("Games/venba/bonus.zip", back.LocalRelativePath, "the bytes are still recorded");
        Assert.Equal(FileState.Verified, back.State, "local state carried, so it is not a gap to queue");
        Assert.False(back.IsOldVersion || back.WithdrawnByGog, "no archive flags on a current record");
    }

    [Test]
    void A_withdrawn_slot_whose_bytes_are_Missing_is_dropped_not_archived()
    {
        var existing = Game(("setup.exe", FileState.Verified, true), ("bonus.zip", FileState.Missing, true));
        LibrarySyncService.MergeIntoForTests(existing, Game(("setup.exe", FileState.NotBackedUp, false)));
        Assert.Equal(1, existing.Files.Count, "gone from the current list");
        Assert.Equal(0, existing.OldVersionFiles.Count, "nothing on disk to keep counted");
    }

    // ---- F8: UNC probe ----------------------------------------------------------------------------------------

    [Test]
    void A_UNC_path_is_classified_and_probed_without_throwing()
    {
        Assert.True(DriveResolver.IsUncPath(@"\\server\share\x"), "two leading backslashes: a share");
        Assert.False(DriveResolver.IsUncPath(@"C:\x"), "a drive letter is not");
        Assert.False(DriveResolver.IsUncPath("/media/u/disk"), "a mount is not");
        var s = DriveResolver.Probe(@"\\server\share\x", fresh: true);
        Assert.False(s.Online, "an unreachable share reads offline, not an exception");
        Assert.Null(DriveResolver.Capacity(@"\\server\share\x"), "offline: no capacity");
        Assert.Null(DriveResolver.For(@"\\server\share\x"), "no drive letter to resolve");
    }

    [Test]
    void A_present_share_with_no_size_figures_is_online_with_unknown_capacity()
    {
        var s = new DriveResolver.DriveState(true, 0, 0, CapacityKnown: false);
        Assert.True(s.Online && !s.CapacityKnown, "present, sizes unknown");
    }

    // ---- F11: saved scope writer --------------------------------------------------------------------------------

    [Test]
    void SavedScope_Set_writes_the_manifest_scope_and_leaves_unnamed_parts_alone()
    {
        var store = new MemoryStore();
        SavedScope.Set(store, includeExtras: false, languages: new[] { "English" });
        var s = store.Current.Scope!;
        Assert.False(s.IncludeExtras); Assert.True(s.IncludeGames, "not named: untouched");
        Assert.True(s.LanguagesChosen && s.Languages.SequenceEqual(new[] { "English" }));
        Assert.False(s.PlatformsChosen, "not named: still unasked");

        SavedScope.Set(store, platforms: Array.Empty<string>());
        Assert.True(store.Current.Scope!.PlatformsChosen && store.Current.Scope.Platforms.Count == 0, "empty list = every platform, chosen");
        Assert.False(store.Current.Scope.IncludeExtras, "earlier answer kept");
    }

    [Test]
    void SavedScope_prune_drops_only_queued_files_the_scope_excludes()
    {
        var store = new MemoryStore();
        var item = new LibraryItem { GogId = 9, Title = "Nine", Slug = "nine" };
        item.Files.Add(new GameFile { GameGogId = 9, FileKey = "win", Kind = FileKind.Installer, Os = "windows", Language = "English", State = FileState.NotBackedUp });
        item.Files.Add(new GameFile { GameGogId = 9, FileKey = "mac", Kind = FileKind.Installer, Os = "mac", Language = "English", State = FileState.NotBackedUp });
        store.Current.Items.Add(item);
        store.Current.Downloads.Enqueue(9, "win"); store.Current.Downloads.Enqueue(9, "mac"); store.Current.Downloads.Enqueue(9, "gone");

        Assert.Equal(0, SavedScope.PruneQueuedOutOfScope(store).Count, "no saved scope: nothing is out of scope");

        SavedScope.Set(store, platforms: new[] { "windows" });
        var dropped = SavedScope.PruneQueuedOutOfScope(store);
        Assert.Equal(1, dropped.Count, "the mac installer left");
        Assert.Equal("mac", dropped[0].FileKey);
        Assert.True(store.Current.Downloads.Contains(9, "win"), "the in-scope file stays queued");
        Assert.False(store.Current.Downloads.Contains(9, "mac"), "the excluded one is gone");
        Assert.True(store.Current.Downloads.Contains(9, "gone"), "an entry with no record is not this prune's to judge");
    }

}
