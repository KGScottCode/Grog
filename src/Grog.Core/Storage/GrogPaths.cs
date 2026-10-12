// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.IO;

namespace Grog.Core.Storage;

public sealed class GrogPaths
{
    public const string ConfigDirName = ".grog";   // drive-side folder name, read only for migration
    public const string ManifestFileName = "grog-manifest.json";
    public const string TokensFileName = "tokens.json";
    public const string SettingsFileName = "app-settings.json";

    public string BackupRoot { get; }

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

    public static string ResolveConfigDir()
    {
        if (Environment.GetEnvironmentVariable("GROG_CONFIG_DIR") is { Length: > 0 } env)
        {
            Directory.CreateDirectory(env);
            return env;
        }

        // Portable is self-contained
        if (PortableDataDir() is { } portable) return portable;

        var profile = ProfileDirOverride
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Grog");

        Directory.CreateDirectory(profile);
        return profile;
    }

    internal static string? ExeDirOverride;

    internal static string? ProfileDirOverride;

    public static string? PortableInstallDir()
        => PortableDataDir() is { } data ? Path.GetDirectoryName(data) : null;

    public const string PortableToken = "{portable}";

    public static string StorePath(string path)
    {
        if (PortableInstallDir() is not { } install) return path;
        var full = Path.GetFullPath(path);
        var root = Path.GetFullPath(install);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return path;
        var rel = Path.GetRelativePath(root, full);
        return rel == "." ? PortableToken : $"{PortableToken}/{rel.Replace('\\', '/')}";
    }

    public static string ResolvePath(string stored)
    {
        if (!stored.StartsWith(PortableToken, StringComparison.Ordinal)) return stored;
        var install = PortableInstallDir()
                      ?? ExeDirOverride
                      ?? Path.GetDirectoryName(Environment.ProcessPath);
        if (string.IsNullOrEmpty(install)) return stored;   // nowhere to anchor
        var rel = stored.Length > PortableToken.Length ? stored[(PortableToken.Length + 1)..] : "";
        return rel.Length == 0 ? install : Path.GetFullPath(Path.Combine(install, rel));
    }

    private static string? PortableDataDir()
    {
        try
        {
            var exeDir = ExeDirOverride ?? Path.GetDirectoryName(Environment.ProcessPath);
            if (string.IsNullOrEmpty(exeDir)) return null;
            var data = Path.Combine(exeDir, "GrogData");
            if (!Directory.Exists(data)) { PortableFallbackReason = null; return null; }

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

    public static string? PortableFallbackReason { get; private set; }
    private static readonly TimeSpan ProbeTtl = TimeSpan.FromSeconds(30);
    public static void ForgetPortableProbe() { lock (ProbeGate) { LastProbe = null; PortableFallbackReason = null; } }

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
