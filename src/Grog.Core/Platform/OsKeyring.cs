// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Grog.Core.Platform;

public enum TokenProtection
{
    FilePermissionsOnly = 0,
    WindowsDataProtection = 1,
    LinuxKeyring = 2,
    MacKeychain = 3,
}

public enum TokenProtectionReason
{
    Encrypted = 0,
    OptedOut = 1,
    KeyringUnavailable = 2,
    KeyNotYetCreated = 3,
}

public static class OsKeyring
{
    private const string ServiceName = "Grog";
    private const string AccountName = "token-encryption-key";

    private static bool? _available;

    public static bool Available => _available ??= Probe();

    private static bool Probe()
    {
        try
        {
            if (OperatingSystem.IsMacOS()) return System.IO.File.Exists("/usr/bin/security");
            if (OperatingSystem.IsLinux())
                return TryRun("secret-tool", $"lookup service {ServiceName} account grog-availability-probe",
                              out var code) && code <= 1;
            return false;
        }
        catch { return false; }
    }

    private static int TimeoutMs
        => !OperatingSystem.IsLinux() ? 10_000
         : (Environment.GetEnvironmentVariable("DISPLAY") is { Length: > 0 }
            || Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is { Length: > 0 }) ? 10_000 : 3_000;

    private static bool TryRun(string exe, string args, out int exitCode)
        => TryRun2(exe, args, out exitCode, out _);

    private static bool TryRun2(string exe, string args, out int exitCode, out string stdout)
    {
        exitCode = -1;
        stdout = "";
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(TimeoutMs)) { try { p.Kill(true); } catch { } return false; }
            stdout = outTask.GetAwaiter().GetResult();
            _ = errTask.GetAwaiter().GetResult();
            exitCode = p.ExitCode;
            return true;
        }
        catch { return false; }
    }

    // Cache model, three remembered outcomes
    private static byte[]? _cachedKey;
    private static DateTimeOffset _failedUntil;
    private static bool _refused;
    private static bool _noKey;
    private static readonly object _gate = new();

    public static Action<string>? Trace;

    private static void Note(string what)
    {
        try
        {
            if (Trace is null) return;
            var frames = Environment.StackTrace.Split('\n')
                .Where(f => !f.Contains("OsKeyring") && !f.Contains("System.Environment"))
                .Take(4).Select(f => f.Trim().Replace("at ", ""));
            Trace($"{what} [thread {Environment.CurrentManagedThreadId}] <- {string.Join(" <- ", frames)}");
        }
        catch { /* diagnostics never throw */ }
    }

    public static void ResetAvailabilityCache() { _failedUntil = default; _refused = false; _noKey = false; _available = null; }

    public static byte[]? GetExistingKey()
    {
        lock (_gate)
        {
            if (_cachedKey is not null) return _cachedKey;
            if (_refused || _noKey || DateTimeOffset.UtcNow < _failedUntil) return null;
            try
            {
                Note("attempt: lookup (GetExistingKey)");
                var existing = Load(out bool infraFailed);
                if (existing is not null) { _available = true; Note("result: key found"); return _cachedKey = existing; }
                if (infraFailed) { _failedUntil = DateTimeOffset.UtcNow.AddSeconds(60); Note("result: infra failure (cached 60s)"); }
                else { _noKey = true; _available = true; Note("result: no key stored (mint on next save)"); }
                return null;
            }
            catch { _failedUntil = DateTimeOffset.UtcNow.AddSeconds(60); return null; }
        }
    }

    public static byte[]? GetOrCreateKey()
    {
        lock (_gate)
        {
            if (_cachedKey is not null) return _cachedKey;
            if (_refused || DateTimeOffset.UtcNow < _failedUntil) return null;
            try
            {
                Note("attempt: lookup (GetOrCreateKey)");
                var existing = Load(out bool infraFailed);
                if (existing is not null) { _available = true; Note("result: key found"); return _cachedKey = existing; }
                if (infraFailed) { _failedUntil = DateTimeOffset.UtcNow.AddSeconds(60); Note("result: infra failure (cached 60s)"); return null; }
                var key = RandomNumberGenerator.GetBytes(32);
                Note("attempt: STORE new key (may prompt)");
                if (Store(key)) { _available = true; Note("result: key stored"); return _cachedKey = key; }
                Note("result: store refused (held until next user action)");
                _refused = true;
                return null;
            }
            catch
            {
                _failedUntil = DateTimeOffset.UtcNow.AddSeconds(60);
                return null;
            }
        }
    }

    /// <param name="infraFailed">True when the keyring INFRASTRUCTURE failed (binary missing, dead bus,
    /// timed-out call) - cacheable. False for a clean "no key / refused" answer - never cached, so a
    /// canceled unlock prompt can be retried by the next user action.</param>
    private static byte[]? Load(out bool infraFailed)
    {
        infraFailed = false;
        string b64;
        if (OperatingSystem.IsMacOS())
        {
            if (!TryRun2("/usr/bin/security",
                     $"find-generic-password -s {ServiceName} -a {AccountName} -w", out var codeM, out b64))
            { infraFailed = true; return null; }
            if (codeM != 0)
            {
                if (TryRun2("/usr/bin/security",
                        $"find-generic-password -s {ServiceName} -a {AccountName}", out var metaCode, out _)
                    && metaCode == 0)
                    infraFailed = true;
                return null;
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            if (!TryRun2("secret-tool", $"lookup service {ServiceName} account {AccountName}", out var codeL, out b64))
            { infraFailed = true; return null; }
            if (codeL != 0) return null;
        }
        else return null;

        b64 = b64.Trim();
        if (b64.Length == 0) return null;
        try { var k = Convert.FromBase64String(b64); return k.Length == 32 ? k : null; }
        catch { return null; }
    }

    private static bool Store(byte[] key)
    {
        var b64 = Convert.ToBase64String(key);
        if (OperatingSystem.IsMacOS())
            return Run("/usr/bin/security",
                       $"add-generic-password -U -s {ServiceName} -a {AccountName} -w {b64}", out _);
        if (OperatingSystem.IsLinux())
            // secret-tool takes the secret on STDIN, never argv -- an argv secret is visible in `ps`.
            return Run("secret-tool", $"store --label=\"Grog token key\" service {ServiceName} account {AccountName}",
                       out _, stdin: b64);
        return false;
    }

    public static string Describe(TokenProtection p) => p switch
    {
        TokenProtection.FilePermissionsOnly => "stored in a file only your user account can read",
        _ => "encrypted on your machine",
    };

    public static string UnlockNote(TokenProtection p) => p switch
    {
        TokenProtection.MacKeychain =>
            " If your Keychain is locked when Grog starts, macOS will ask for your login password to"
          + " unlock it -- that prompt comes from your system, not from Grog.",
        TokenProtection.LinuxKeyring =>
            " If the keyring is locked when Grog starts, your system will ask for your login password to"
          + " unlock it -- that prompt comes from your system, not from Grog.",
        _ => "",
    };

    private static bool Run(string exe, string args, out string stdout, bool allowFailure = false, string? stdin = null)
    {
        stdout = "";
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = stdin is not null,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            if (stdin is not null) { p.StandardInput.Write(stdin); p.StandardInput.Close(); }
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(TimeoutMs)) { try { p.Kill(true); } catch { } return false; }
            stdout = outTask.GetAwaiter().GetResult();
            _ = errTask.GetAwaiter().GetResult();
            return p.ExitCode == 0;
        }
        catch when (allowFailure) { return false; }
        catch { return false; }
    }
}
