// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Grog.Core.Platform;

/// <summary>How the token file on disk is actually protected: a value the UI reads, so the app's claim
/// about encryption is true on every platform rather than a sentence written once.</summary>
public enum TokenProtection
{
    /// <summary>Chmod 600 and nothing else. Honest fallback, never described as encrypted.</summary>
    FilePermissionsOnly = 0,
    /// <summary>Windows DPAPI, CurrentUser scope.</summary>
    WindowsDataProtection = 1,
    /// <summary>AES-256-GCM with the key held in the login keyring (GNOME Keyring, KWallet, anything
    /// implementing the freedesktop Secret Service).</summary>
    LinuxKeyring = 2,
    /// <summary>AES-256-GCM with the key held in the macOS Keychain.</summary>
    MacKeychain = 3,
}

/// <summary>WHY protection is what it is. FilePermissionsOnly has three distinct causes and each needs a
/// different sentence in the UI: the opt-out was the user's choice, a missing keyring has an install
/// remedy, and a fresh machine just has not saved yet. One sentence for all three told two of them
/// something false.</summary>
public enum TokenProtectionReason
{
    /// <summary>Protection is not FilePermissionsOnly; there is nothing to explain.</summary>
    Encrypted = 0,
    /// <summary>The scheduled-tasks marker file is present: the user chose plaintext so cron/systemd
    /// runs can read the token.</summary>
    OptedOut = 1,
    /// <summary>No keyring this code can drive is reachable (no libsecret, no session bus).</summary>
    KeyringUnavailable = 2,
    /// <summary>A keyring is reachable but holds no Grog key yet -- it is minted on the next save.
    /// Also the answer while a locked keyring's unlock prompt stands canceled.</summary>
    KeyNotYetCreated = 3,
}

/// <summary>
/// The OS keyring holds one thing: a 256-bit key that encrypts the token file. A key, not the token,
/// because the token must stay portable beside the config while a keyring is machine-bound and path-blind.
/// Shells out to <c>secret-tool</c> (Linux) / <c>security</c> (macOS); <see cref="Available"/> is a probe.
/// </summary>
public static class OsKeyring
{
    private const string ServiceName = "Grog";
    private const string AccountName = "token-encryption-key";

    /// <summary>Cached so a missing tool costs one failed process launch per run, not one per save.</summary>
    private static bool? _available;

    /// <summary>True when a keyring this code can drive is actually reachable. A probe, because the answer
    /// depends on the machine: no libsecret installed, no session bus over SSH, a locked keyring the user
    /// cancels -- all of them are normal and all of them mean "fall back and say so".</summary>
    public static bool Available => _available ??= Probe();

    private static bool Probe()
    {
        try
        {
            // macOS: the binary's presence is the fact. `security -h` prints usage and its exit code is not
            // documented as 0, so a probe keyed on it could read a healthy Mac as "no keyring" until the first
            // real lookup flipped it (09-13). Every macOS install ships /usr/bin/security and a login Keychain.
            if (OperatingSystem.IsMacOS()) return System.IO.File.Exists("/usr/bin/security");
            if (OperatingSystem.IsLinux())
                // secret-tool has no --version flag (usage + exit 2), so probe with a harmless lookup:
                // exit 0/1 proves the binary launched and answered; a missing binary fails the launch.
                // A dead session bus also exits 1 and reads as available -- acceptable, since every real
                // store/lookup degrades per-call and callers fall back to FilePermissionsOnly.
                return TryRun("secret-tool", $"lookup service {ServiceName} account grog-availability-probe",
                              out var code) && code <= 1;
            return false;
        }
        catch { return false; }
    }

    /// <summary>The per-call bound: 10s where a display exists (an unlock prompt needs time to be answered;
    /// 10s is the ceiling in all cases), 3s headless (cron/SSH -- nothing can answer a prompt).
    /// macOS keeps 10s (`security` never depends on a display).</summary>
    private static int TimeoutMs
        => !OperatingSystem.IsLinux() ? 10_000
         : (Environment.GetEnvironmentVariable("DISPLAY") is { Length: > 0 }
            || Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is { Length: > 0 }) ? 10_000 : 3_000;

    /// <summary>Like <see cref="Run"/> but reports the exit code instead of collapsing it to success,
    /// for probes where a non-zero exit is still a meaningful answer. False = process failed to launch.</summary>
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
            // Wait (bounded) BEFORE read: ReadToEnd blocks until the child closes stdout, so read-first
            // would defeat the timeout while a bus-less secret-tool holds the pipe. Outputs are tens of
            // bytes; the pipes cannot fill, so reading after exit (or after the kill) is safe.
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

    /// <summary>The key for encrypting the token file, created on first use. Null when no keyring is
    /// reachable -- callers fall back to plaintext and report <see cref="TokenProtection.FilePermissionsOnly"/>
    /// rather than inventing a key that would have to live in the clear beside the thing it protects.</summary>
    // Cache model, three remembered outcomes:
    //   - _cachedKey: success, held for the process (the machine key never changes mid-run).
    //   - _failedUntil: infrastructure failure (tool missing, dead bus, timeout), 60s TTL.
    //   - _refused: a clean "no key / user canceled the prompt" answer, held until the next user action
    //     resets it. One user gesture = at most one unlock prompt, and a render (navigation, row rebuild)
    //     may never trigger a prompt - _refused plus the reset-on-action contract guarantees both.
    private static byte[]? _cachedKey;
    private static DateTimeOffset _failedUntil;
    private static bool _refused;
    // A clean "no key stored" answer from a READ. Distinct from _refused ON PURPOSE: no prompt was shown
    // and nothing was declined -- it is a mint-on-next-save condition. It holds READS (renders must not
    // re-spawn secret-tool per binding) but must NEVER hold GetOrCreateKey: conflating the two made a
    // startup readout block the login's key mint, and a healthy keyring wrote plaintext (Linux walk
    // 2026-08-23 R1/R3).
    private static bool _noKey;
    // Serializes every attempt: startup runs token reads concurrently (async restore plus UI bindings
    // evaluating Protection), and two in-flight secret-tool calls would queue two unlock prompts before
    // either could set _refused. The second caller waits on the first and reads the refusal instead.
    private static readonly object _gate = new();

    /// <summary>Diagnostic hook: every actual keyring process spawn (and its classification) is reported
    /// here when wired (the App wires it to grog.log at startup), naming each spawn's call path.
    /// Cheap: fires only when a process launches, never per cached read.</summary>
    public static Action<string>? Trace;

    private static void Note(string what)
    {
        try
        {
            if (Trace is null) return;
            // Top of the RELEVANT stack: skip the Note/keyring frames, keep the first few callers.
            var frames = Environment.StackTrace.Split('\n')
                .Where(f => !f.Contains("OsKeyring") && !f.Contains("System.Environment"))
                .Take(4).Select(f => f.Trim().Replace("at ", ""));
            Trace($"{what} [thread {Environment.CurrentManagedThreadId}] <- {string.Join(" <- ", frames)}");
        }
        catch { /* diagnostics never throw */ }
    }

    /// <summary>Forget cached failures AND refusals, so a USER-TRIGGERED action (Connect, Rescan, an
    /// explicit login) gets one fresh chance at the keyring - including re-raising the unlock prompt
    /// the user may have canceled earlier. Never called from render paths.</summary>
    public static void ResetAvailabilityCache() { _failedUntil = default; _refused = false; _noKey = false; _available = null; }

    /// <summary>The decrypt-path key: returns the existing key or null, NEVER creates one. A new key cannot
    /// decrypt an existing file, and the create attempt is a second secret-tool call -- on a locked keyring
    /// that is a second unlock prompt per restore attempt.</summary>
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

    /// <summary>The encrypt-path key: existing key, or a newly created one. No separate Available probe --
    /// on a locked keyring every secret-tool call can raise the unlock prompt, so Load itself answers
    /// availability (launch failure = tool missing/bus dead).</summary>
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
                // Store on a locked keyring = the user declined (or the collection is locked): a refusal,
                // not infrastructure - the next user action may retry it.
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
                // An item can EXIST and still refuse its secret: Keychain metadata stays readable while the
                // keychain is LOCKED, the secret does not. Telling those two apart is not a nicety. Grog
                // looks its key up by service/account only, never by keychain, so calling a locked item
                // "absent" mints a SECOND key into the default keychain while the locked one goes on
                // winning the search list; every later read finds it, fails, and mints again. That is a
                // permanent signed-out loop with nothing in the UI to explain it (macOS walk 2026-09-11,
                // reproduced by accident). Treat unreadable-but-present as INFRASTRUCTURE, so nothing is
                // stored and the next user action retries once the keychain is unlocked.
                // One extra spawn, only on a path that has already failed.
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
            // -U updates in place if an entry already exists, so a re-run cannot orphan a second key.
            // The key rides on argv: `security` has no stdin form (without -w it prompts on /dev/tty, not
            // stdin), so it is visible in `ps` for the milliseconds the process lives. Accepted (09-13): the
            // key only protects the token file against another local user reading it, and a local user who can
            // race `ps` against this call already runs code as us.
            return Run("/usr/bin/security",
                       $"add-generic-password -U -s {ServiceName} -a {AccountName} -w {b64}", out _);
        if (OperatingSystem.IsLinux())
            // secret-tool takes the secret on STDIN, never argv -- an argv secret is visible in `ps`.
            return Run("secret-tool", $"store --label=\"Grog token key\" service {ServiceName} account {AccountName}",
                       out _, stdin: b64);
        return false;
    }

    /// <summary>One short phrase for the UI: all encrypting mechanisms share "encrypted on your machine",
    /// while the unencrypted fallback must never borrow that wording. The enum still carries the mechanism
    /// for anywhere that genuinely needs it.</summary>
    public static string Describe(TokenProtection p) => p switch
    {
        TokenProtection.FilePermissionsOnly => "stored in a file only your user account can read",
        _ => "encrypted on your machine",
    };

    /// <summary>One extra sentence for keyring-backed platforms, empty elsewhere: the OS-branded unlock
    /// prompt can appear before any Grog window exists, so the sign-in reassurance says who is asking and
    /// why. Silent on Windows and the plain-file fallback -- warning about a prompt nobody sees is noise.</summary>
    // Platform VOCABULARY, not just wording: the badge beside this sentence reads KEYCHAIN on macOS, and a
    // body that then says "keyring" reads as a different thing entirely. Keep each platform's own noun.
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
            // Wait (bounded) before read - as in TryRun2, read-first would defeat the timeout.
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            // A keyring prompt can block; bound it so a locked keyring cannot hang a headless run forever.
            if (!p.WaitForExit(TimeoutMs)) { try { p.Kill(true); } catch { } return false; }
            stdout = outTask.GetAwaiter().GetResult();
            _ = errTask.GetAwaiter().GetResult();
            return p.ExitCode == 0;
        }
        catch when (allowFailure) { return false; }
        catch { return false; }
    }
}
