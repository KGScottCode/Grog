// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.IO;
using Grog.Core.Storage;
using Grog.Core.Tests.Framework;

namespace Grog.Core.Tests;

/// <summary>
/// Portable mode is a present-tense fact about the folder: a Data directory beside the executable makes
/// that folder the whole install (the VS Code convention; the ZIP ships one, the installer never does).
/// These tests pin the resolution order and the two safety behaviors: an unwritable Data folder falls
/// back instead of failing, and a ZIP user upgrading in place gets their profile state copied in once.
/// </summary>
[Trait("storage")]
public sealed class PortableModeTests
{
    private string _root = "";
    private string? _envBefore, _xdgBefore;

    [Setup]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "grog-port-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        // The resolver's higher and lower precedence levels both point into the sandbox: env cleared so
        // portable can win, profile redirected so migration reads a folder this test owns.
        _envBefore = Environment.GetEnvironmentVariable("GROG_CONFIG_DIR");
        _xdgBefore = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("GROG_CONFIG_DIR", null);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(_root, "_profilehome"));
        // XDG redirects the profile dir on Linux only; Windows ignores env vars for ApplicationData,
        // so the seam is what makes the migration read a folder this test owns on every OS.
        GrogPaths.ProfileDirOverride = Path.Combine(_root, "_profilehome", "Grog");
        GrogPaths.ExeDirOverride = Path.Combine(_root, "install");
        Directory.CreateDirectory(GrogPaths.ExeDirOverride);
    }

    [Teardown]
    public void Teardown()
    {
        Environment.SetEnvironmentVariable("GROG_CONFIG_DIR", _envBefore);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", _xdgBefore);
        GrogPaths.ProfileDirOverride = null;
        GrogPaths.ExeDirOverride = null;
        GrogPaths.ForgetPortableProbe();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string InstallDir => GrogPaths.ExeDirOverride!;
    private string DataDir => Path.Combine(InstallDir, "GrogData");

    [Test]
    void No_data_folder_means_the_profile_dir_as_always()
    {
        var dir = GrogPaths.ResolveConfigDir();
        Assert.True(!dir.StartsWith(InstallDir), "without a Data folder nothing lives beside the exe");
    }

    [Test]
    void A_data_folder_beside_the_exe_is_the_config_dir()
    {
        Directory.CreateDirectory(DataDir);
        Assert.Equal(DataDir, GrogPaths.ResolveConfigDir(), "the Data folder IS the install's state");
    }

    [Test]
    void The_env_override_outranks_portable()
    {
        Directory.CreateDirectory(DataDir);
        var forced = Path.Combine(_root, "forced");
        Environment.SetEnvironmentVariable("GROG_CONFIG_DIR", forced);
        Assert.Equal(forced, GrogPaths.ResolveConfigDir(),
            "GROG_CONFIG_DIR stays the top of the order; portable slots under it");
    }

    [Test]
    void An_unwritable_data_folder_is_ignored_not_fatal()
    {
        // KeePassXC's refinement: a ZIP dropped somewhere read-only must fall back to the profile dir,
        // never crash on first save. Skipped where the permission bits cannot make a dir unwritable (root).
        Directory.CreateDirectory(DataDir);
        if (OperatingSystem.IsWindows()) { Assert.SkipUnless(false, "unix permission test"); return; }
        File.SetUnixFileMode(DataDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        GrogPaths.ForgetPortableProbe();
        try
        {
            var dir = GrogPaths.ResolveConfigDir();
            Assert.SkipUnless(dir != DataDir || !CanActuallyWrite(DataDir), "running as root: everything is writable");
            Assert.True(dir != DataDir, "a read-only Data folder does not become the config dir");
        }
        finally
        {
            File.SetUnixFileMode(DataDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            GrogPaths.ForgetPortableProbe();
        }
    }

    private static bool CanActuallyWrite(string dir)
    {
        try { var p = Path.Combine(dir, ".p"); File.WriteAllText(p, ""); File.Delete(p); return true; }
        catch { return false; }
    }

    [Test]
    void A_chosen_root_under_the_install_is_remembered_relative_and_survives_a_move()
    {
        // The USB story: an absolute ChosenRoot dies with its drive letter. Under a portable install the
        // path is stored as a {portable} token and resolved against wherever the install is mounted NOW.
        Directory.CreateDirectory(DataDir);
        var root = Path.Combine(InstallDir, "GOG_Library");

        var stored = GrogPaths.StorePath(root);
        Assert.True(stored.StartsWith(GrogPaths.PortableToken), "under the install folder, remembered relative");

        // Simulate the stick mounting somewhere else: move the whole install.
        var newInstall = Path.Combine(_root, "mounted-elsewhere");
        Directory.Move(InstallDir, newInstall);
        GrogPaths.ExeDirOverride = newInstall;

        Assert.Equal(Path.Combine(newInstall, "GOG_Library"), GrogPaths.ResolvePath(stored),
            "the token re-anchors to the new mount; no drive letter was ever stored");
    }

    [Test]
    void Paths_outside_the_install_and_non_portable_installs_stay_absolute()
    {
        Directory.CreateDirectory(DataDir);
        var elsewhere = Path.Combine(_root, "big-disk", "GOG_Library");
        Assert.Equal(elsewhere, GrogPaths.StorePath(elsewhere),
            "a root on another drive is not part of the stick; it stays absolute");

        Directory.Delete(DataDir);   // not portable any more
        var inside = Path.Combine(InstallDir, "GOG_Library");
        Assert.Equal(inside, GrogPaths.StorePath(inside),
            "without portable mode nothing is tokenised, installed behavior is untouched");
        Assert.Equal(elsewhere, GrogPaths.ResolvePath(elsewhere), "absolute paths resolve to themselves");
    }

    [Test]
    void The_portable_default_backup_root_lives_inside_the_install()
    {
        Directory.CreateDirectory(DataDir);
        Assert.True(BackupLocationSuggester.DefaultRoot().StartsWith(InstallDir),
            "portable defaults keep app, state and backups as ONE movable folder");

        Directory.Delete(DataDir);
        Assert.True(!BackupLocationSuggester.DefaultRoot().StartsWith(InstallDir),
            "and the installed default is elsewhere, exactly as before");
    }

    [Test]
    void A_portable_copy_starts_empty_and_never_reads_the_profile()
    {
        // The rule: portable and installed have NO concept of each other. A portable copy launched on a
        // machine with an installed setup starts blank -- it must not inherit the manifest, the settings,
        // and above all not the tokens, which would put GOG credentials on a stick nobody asked to.
        var profile = Path.Combine(_root, "_profilehome", "Grog");
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, "grog-manifest.json"), "{\"items\":[]}");
        File.WriteAllText(Path.Combine(profile, "tokens-goguser.json"), "{}");
        File.WriteAllText(Path.Combine(profile, "app-settings.json"), "{}");

        Directory.CreateDirectory(DataDir);
        Assert.Equal(DataDir, GrogPaths.ResolveConfigDir(), "portable wins the resolution order");

        Assert.True(!File.Exists(Path.Combine(DataDir, "grog-manifest.json")), "no manifest is copied in");
        Assert.True(!File.Exists(Path.Combine(DataDir, "tokens-goguser.json")), "and never the sign-in tokens");
        Assert.True(!File.Exists(Path.Combine(DataDir, "app-settings.json")), "nor the settings");
    }

    [Test]
    void A_portable_launch_leaves_the_profile_untouched()
    {
        // The other direction of the same rule: running portable must not create or write the profile
        // folder, so a stick used on someone else's machine leaves no trace of Grog behind.
        var profile = Path.Combine(_root, "_profilehome", "Grog");
        Directory.CreateDirectory(DataDir);

        GrogPaths.ResolveConfigDir();

        Assert.True(!Directory.Exists(profile), "a portable run never touches the host's profile folder");
    }
}
