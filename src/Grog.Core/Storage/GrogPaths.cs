// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.IO;

namespace Grog.Core.Storage;

/// <summary>Single source of truth for where Grog keeps things: Grog state (manifest, tokens, changelogs)
/// lives in the per-user profile folder (<c>%APPDATA%/Grog</c>), the backup folder holds only content.
/// The backup root is only ever an explicit user-given path -- no env-var or exe-dir fallback, Grog never
/// guesses where backups live; absent a root, content operations must require one.</summary>
public sealed class GrogPaths
{
    public const string ConfigDirName = ".grog";   // drive-side folder name, read only for migration
    public const string ManifestFileName = "grog-manifest.json";
    public const string TokensFileName = "tokens.json";
    public const string SettingsFileName = "app-settings.json";

    /// <summary>The backup root: where game content folders live.</summary>
    public string BackupRoot { get; }

    /// <summary>The config dir (<c>%APPDATA%/Grog</c>): manifest, tokens, changelogs, Grog state.</summary>
    public string ConfigDir { get; }

    public string ManifestPath => Path.Combine(ConfigDir, ManifestFileName);
    public string TokensPath => Path.Combine(ConfigDir, TokensFileName);
    public string ArtDir => Path.Combine(ConfigDir, "art");
    public string SettingsPath => Path.Combine(ConfigDir, SettingsFileName);

    private GrogPaths(string backupRoot, string configDir)
    {
        BackupRoot = backupRoot;
        ConfigDir = configDir;
    }

    /// <summary>Resolves paths for the given (optional) explicit backup root. The config dir is always
    /// the per-user profile folder. Ensures it exists and migrates any legacy drive-side state once.</summary>
    /// <summary>The same answer as <see cref="Resolve"/> WITHOUT the one-time legacy-state probe of the backup
    /// root. For hot paths (a 1 Hz dashboard tick) on a machine whose root is a removable drive: the probe is a
    /// Directory.Exists on that drive, which a saturated USB stick answers in hundreds of ms (measured 09-05).
    /// Callers that already resolved once this session lose nothing: migration ran then.</summary>
    public static GrogPaths ResolveNoProbe(string? explicitRoot = null)
    {
        var root = string.IsNullOrWhiteSpace(explicitRoot) ? "" : Path.GetFullPath(explicitRoot);
        return new GrogPaths(root, ResolveConfigDir());
    }

    public static GrogPaths Resolve(string? explicitRoot = null)
    {
        // Explicit only -- no env var, no exe-dir fallback. Empty when not given (content ops must require one).
        var root = string.IsNullOrWhiteSpace(explicitRoot) ? "" : Path.GetFullPath(explicitRoot);

        var configDir = ResolveConfigDir();

        if (root.Length > 0) MigrateLegacyDriveState(root, configDir);
        return new GrogPaths(root, configDir);
    }

    /// <summary>The config dir on its own, for callers that need a path before a backup root exists (the
    /// settings file is what remembers the root). The SINGLE resolver -- no caller may re-derive the path,
    /// or the modes below would split the state folder in two.
    ///
    /// <para>Resolution order: GROG_CONFIG_DIR, then PORTABLE MODE, then the profile folder. Portable mode
    /// is the VS Code convention, named for the app: a <c>GrogData</c> folder beside the executable makes THIS folder the whole
    /// install -- the release ZIP ships one, the installer does not, so "installed" vs "portable" is a
    /// present-tense fact about the folder rather than a tracked claim about the past. KeePassXC's
    /// refinement is kept too: an unwritable Data folder (ZIP dropped into Program Files, read-only media)
    /// is IGNORED rather than failed on, and the profile folder serves as usual.</para></summary>
    public static string ResolveConfigDir()
    {
        if (Environment.GetEnvironmentVariable("GROG_CONFIG_DIR") is { Length: > 0 } env)
        {
            Directory.CreateDirectory(env);
            return env;
        }

        // PORTABLE IS SELF-CONTAINED: a portable copy never reads, copies from, or writes to the profile
        // folder, and an installed copy never learns a portable one exists. Neither has any concept of the
        // other. Carrying an existing setup across is a deliberate, manual act (copy the three state files
        // into GrogData yourself), never something the app does on your behalf -- it would otherwise put
        // GOG tokens on a USB stick nobody asked to put them on, and leave two copies silently diverging.
        if (PortableDataDir() is { } portable) return portable;

        var profile = ProfileDirOverride
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Grog");

        Directory.CreateDirectory(profile);
        return profile;
    }

    /// <summary>Tests only: stands in for the executable's directory, which under the test runner is the
    /// runner's own folder and can never legitimately contain a Data dir.</summary>
    internal static string? ExeDirOverride;

    /// <summary>Tests only: stands in for the profile config folder. XDG_CONFIG_HOME redirects it on
    /// Linux, but Windows resolves ApplicationData through the shell API and ignores env vars, so a
    /// test that seeds "profile" state needs this seam to be cross-platform.</summary>
    internal static string? ProfileDirOverride;

    /// <summary>The install folder when running PORTABLE (a writable Data dir beside the exe), else null.
    /// Public because portable changes two more answers than the config dir: the default backup root, and
    /// how a chosen root under this folder is remembered.</summary>
    public static string? PortableInstallDir()
        => PortableDataDir() is { } data ? Path.GetDirectoryName(data) : null;

    /// <summary>Prefix for a remembered path stored RELATIVE to the install folder, so a portable copy
    /// survives a drive-letter or mount-point change. An absolute PathHint would point at the old letter;
    /// the token survives because it names no drive at all.</summary>
    public const string PortableToken = "{portable}";

    /// <summary>How a chosen path should be REMEMBERED: under a portable install it becomes
    /// "{portable}/relative", anything else stays absolute and untouched.</summary>
    public static string StorePath(string path)
    {
        if (PortableInstallDir() is not { } install) return path;
        var full = Path.GetFullPath(path);
        var root = Path.GetFullPath(install);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return path;
        var rel = Path.GetRelativePath(root, full);
        return rel == "." ? PortableToken : $"{PortableToken}/{rel.Replace('\\', '/')}";
    }

    /// <summary>The inverse of <see cref="StorePath"/>: a token path resolves against WHEREVER the install
    /// folder is mounted right now. A token remembered by a portable copy that is no longer portable (Data
    /// deleted) resolves against the exe's folder anyway -- the folder the token always meant.</summary>
    public static string ResolvePath(string stored)
    {
        if (!stored.StartsWith(PortableToken, StringComparison.Ordinal)) return stored;
        var install = PortableInstallDir()
                      ?? ExeDirOverride
                      ?? Path.GetDirectoryName(Environment.ProcessPath);
        if (string.IsNullOrEmpty(install)) return stored;   // nowhere to anchor; hand the token back unresolved
        var rel = stored.Length > PortableToken.Length ? stored[(PortableToken.Length + 1)..] : "";
        return rel.Length == 0 ? install : Path.GetFullPath(Path.Combine(install, rel));
    }

    /// <summary>The portable Data folder beside the executable, or null when absent or not writable.</summary>
    private static string? PortableDataDir()
    {
        try
        {
            var exeDir = ExeDirOverride ?? Path.GetDirectoryName(Environment.ProcessPath);
            if (string.IsNullOrEmpty(exeDir)) return null;
            var data = Path.Combine(exeDir, "GrogData");
            if (!Directory.Exists(data)) { PortableFallbackReason = null; return null; }

            // Writability is the gate, not existence: probing with a real file is the only honest test,
            // and a read-only Data folder must mean "fall back", never "crash on first save".
            // The probe is a write + delete on the install medium (a USB stick, portable), and ResolveConfigDir
            // is called from the App's 1 Hz dashboard pass: remembered per folder for a while (review 09-06).
            lock (ProbeGate)
            {
                if (LastProbe is { } lp && lp.Data == data && DateTime.UtcNow - lp.When < ProbeTtl) return lp.Writable ? data : null;
                bool writable;
                try
                {
                    var probe = Path.Combine(data, ".writable-probe");
                    File.WriteAllText(probe, "");
                    File.Delete(probe);
                    writable = true;
                }
                catch { writable = false; }
                LastProbe = (data, writable, DateTime.UtcNow);
                PortableFallbackReason = writable ? null
                    : $"GrogData at {data} is not writable; Grog state is kept in the profile folder instead.";
                return writable ? data : null;
            }
        }
        catch { return null; }
    }
    private static readonly object ProbeGate = new();
    private static (string Data, bool Writable, DateTime When)? LastProbe;

    /// <summary>Why a GrogData folder beside the exe was NOT used (read-only media), for the host to log once. Null
    /// when portable mode is active or no GrogData folder exists.</summary>
    public static string? PortableFallbackReason { get; private set; }
    private static readonly TimeSpan ProbeTtl = TimeSpan.FromSeconds(30);
    /// <summary>Forget the remembered writability probe (tests that flip permissions; a "make portable" action).</summary>
    public static void ForgetPortableProbe() { lock (ProbeGate) { LastProbe = null; PortableFallbackReason = null; } }

    /// <summary>One-time, best-effort migration: copies manifest + token files from <c>&lt;root&gt;/.grog</c>
    /// into the profile folder when the profile has none. Never deletes the old copy.</summary>
    private static void MigrateLegacyDriveState(string root, string configDir)
    {
        try
        {
            var legacy = Path.Combine(root, ConfigDirName);
            if (!Directory.Exists(legacy)) return;
            if (File.Exists(Path.Combine(configDir, ManifestFileName))) return;   // profile already has state

            foreach (var f in Directory.EnumerateFiles(legacy))
            {
                var name = Path.GetFileName(f);
                var isState = name == ManifestFileName || name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)
                              || name.StartsWith("tokens", StringComparison.OrdinalIgnoreCase);
                if (!isState) continue;
                var dest = Path.Combine(configDir, name);
                if (!File.Exists(dest)) File.Copy(f, dest);
            }
        }
        catch { /* best-effort; a fresh login/scan recovers if this fails */ }
    }
}
