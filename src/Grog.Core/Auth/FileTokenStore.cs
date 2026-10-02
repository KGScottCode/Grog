// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using Grog.Core.Platform;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Grog.Core.Auth;

/// <summary>Stores the auth session as JSON in the user's config directory (%APPDATA%\Grog on Windows,
/// ~/.config/Grog elsewhere), encrypted at rest where possible, chmod 600 on unix.</summary>
public sealed class FileTokenStore : ITokenStore
{
    private readonly string _path;

    public FileTokenStore(string? overridePath = null)
    {
        _path = overridePath ?? DefaultPath();
    }

    /// <summary>The default token path. Must delegate to GrogPaths -- the one authority for the config dir
    /// and its casing; a divergent default is a phantom-logout trap on case-sensitive filesystems.</summary>
    public static string DefaultPath() => Grog.Core.Storage.GrogPaths.Resolve().TokensPath;

    public async Task<AuthSession?> LoadAsync()
    {
        if (!File.Exists(_path)) return null;
        try
        {
            var raw = await File.ReadAllBytesAsync(_path);
            var json = Decrypt(raw);
            return JsonSerializer.Deserialize<AuthSession>(json);
        }
        catch (JsonException)
        {
            return null; // corrupt store == not logged in; next login overwrites
        }
        catch
        {
            return null;
        }
    }

    public async Task SaveAsync(AuthSession session)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var json = JsonSerializer.SerializeToUtf8Bytes(session, new JsonSerializerOptions { WriteIndented = true });
        var payload = Encrypt(json);
        // A random temp name per save: two stores on one path (or two saves in flight) never share it.
        var tmp = _path + "." + Path.GetRandomFileName() + ".tmp";
        // Any step that throws (write, chmod, move) leaves no stray .tmp behind.
        try
        {
            await File.WriteAllBytesAsync(tmp, payload);
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite); // 600
            File.Move(tmp, _path, overwrite: true);
        }
        catch { try { File.Delete(tmp); } catch { } throw; }
        ForgetReadable(_path);
    }

    // Encryption at rest: Windows uses DPAPI (CurrentUser); Linux/macOS use AES-256-GCM with the key in the
    // login keyring (OsKeyring); no keyring reachable means plaintext + chmod 600, reported honestly by
    // Protection. Each format carries its own magic header and every reader accepts every format; formats
    // are never removed -- a token that cannot be read is a silent logout.
    private static readonly byte[] Magic = System.Text.Encoding.ASCII.GetBytes("GRGP1\n");     // DPAPI
    private static readonly byte[] MagicGcm = System.Text.Encoding.ASCII.GetBytes("GRGK1\n");  // AES-GCM + keyring

    // Scheduled-tasks escape hatch: keyring-encrypted tokens are unreadable without a session bus (cron,
    // systemd timers, SSH), so the user can opt into plaintext+600 (Accounts toggle, non-Windows only).
    // The choice is a MARKER FILE, not an AppSettings field: the CLI never reads AppSettings, and the
    // marker must be honored by every writer on this machine.
    private const string PlainTextMarkerName = "token-plaintext";
    internal static string? MarkerPathOverride;   // tests only; the real path follows the config dir
    private static string MarkerPath
        => MarkerPathOverride ?? Path.Combine(Grog.Core.Storage.GrogPaths.Resolve().ConfigDir, PlainTextMarkerName);

    /// <summary>True when this machine opted out of keyring encryption so scheduled CLI runs can read the
    /// token. Checked fresh on every use -- no cache -- so the toggle takes effect immediately.</summary>
    public static bool PlainTextPreferred
    {
        get { try { return File.Exists(MarkerPath); } catch { return false; } }
    }

    /// <summary>Create or remove the marker. Callers that flip this should re-save any existing sessions so
    /// the bytes on disk match the new policy at once rather than on some future token refresh.</summary>
    public static void SetPlainTextPreferred(bool on)
    {
        var path = MarkerPath;
        if (on)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "Grog stores sign-in tokens unencrypted on this machine so scheduled" +
                                    " CLI runs (cron, systemd timers) can read them. Delete this file or" +
                                    " turn the Accounts page toggle off to re-enable keyring encryption.\n");
        }
        else if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>How this machine is actually protecting the token. Read by the UI so what it tells the user is
    /// derived from the running system rather than from a sentence written on a Windows box.</summary>
    public static TokenProtection Protection => ProtectionOf(DefaultPath());

    /// <summary>How the token at <paramref name="path"/> is actually protected. When the file exists its own
    /// header is the answer -- the keyring can say "encrypted" over a file a keyring blip wrote in the clear.
    /// Only with no file does the keyring state predict what the next save will do.</summary>
    public static TokenProtection ProtectionOf(string path)
    {
        if (PlainTextPreferred) return TokenProtection.FilePermissionsOnly;
        if (ReadHeader(path) is { } head)
        {
            if (head.AsSpan().SequenceEqual(Magic)) return TokenProtection.WindowsDataProtection;
            if (head.AsSpan().SequenceEqual(MagicGcm))
                return OperatingSystem.IsMacOS() ? TokenProtection.MacKeychain : TokenProtection.LinuxKeyring;
            return TokenProtection.FilePermissionsOnly;
        }
        if (OperatingSystem.IsWindows()) return TokenProtection.WindowsDataProtection;
        // GetEXISTINGKey: a readout must not create keys as a side effect (that causes double keyring
        // prompts); the key is minted only on SaveAsync. A fresh machine honestly reports
        // FilePermissionsOnly until the first save -- nothing is encrypted yet.
        if (OsKeyring.GetExistingKey() is null) return TokenProtection.FilePermissionsOnly;
        return OperatingSystem.IsMacOS() ? TokenProtection.MacKeychain : TokenProtection.LinuxKeyring;
    }

    /// <summary>The file's first six bytes, or null when there is no file (or it cannot be read).</summary>
    private static byte[]? ReadHeader(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var head = new byte[6];
            int n;
            using (var fs = File.OpenRead(path)) n = fs.Read(head, 0, 6);
            return n == 6 ? head : Array.Empty<byte>();
        }
        catch { return null; }
    }

    /// <summary>Per-instance readout of <see cref="ProtectionOf"/> for this store's own file.</summary>
    public TokenProtection FileProtection => ProtectionOf(_path);

    /// <summary>Test seam: the key source behind <see cref="Encrypt"/>. Null means "no keyring".</summary>
    internal static Func<byte[]?> KeySource = OsKeyring.GetOrCreateKey;

    /// <summary>Wired by the host: one line the first time a save falls back to plaintext because the keyring
    /// gave no key. Silent fallback hid a plaintext token behind an "encrypted" badge.</summary>
    public static Action<string>? Log;
    private static int _plaintextFallbackLogged;
    /// <summary>True once this process wrote a token in the clear because no keyring key was available.</summary>
    public static bool PlaintextFallbackHappened => Volatile.Read(ref _plaintextFallbackLogged) != 0;

    /// <summary>WHY <see cref="Protection"/> says what it says, so the UI can tell each fallback cause its
    /// own true sentence. Decided the same way as Protection: an encrypted header on disk answers Encrypted
    /// whatever the keyring says now (the file IS encrypted even when its key is unreachable). Only a
    /// plaintext or missing file consults the keyring, and only through Available, which never prompts.</summary>
    public static TokenProtectionReason ProtectionReason => ProtectionReasonOf(DefaultPath());

    /// <summary>The reason for <see cref="ProtectionOf"/>'s answer for the token at <paramref name="path"/>.</summary>
    public static TokenProtectionReason ProtectionReasonOf(string path)
    {
        if (ProtectionOf(path) != TokenProtection.FilePermissionsOnly) return TokenProtectionReason.Encrypted;
        if (PlainTextPreferred) return TokenProtectionReason.OptedOut;
        if (!OsKeyring.Available) return TokenProtectionReason.KeyringUnavailable;
        return TokenProtectionReason.KeyNotYetCreated;
    }

    private static byte[] Encrypt(byte[] json)
    {
        if (PlainTextPreferred) return json;   // the scheduled-tasks opt-out; chmod 600 still applies

        if (OperatingSystem.IsWindows())
        {
            var enc = System.Security.Cryptography.ProtectedData.Protect(
                json, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            return Prefix(Magic, enc);
        }

        // AES-GCM: nonce (12) + tag (16) + ciphertext after the header. GCM over CBC because it
        // authenticates: a tampered token file fails to decrypt instead of sending garbage to GOG.
        if (KeySource() is { } key)
        {
            var nonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(12);
            var tag = new byte[16];
            var cipher = new byte[json.Length];
            using (var gcm = new System.Security.Cryptography.AesGcm(key, 16))
                gcm.Encrypt(nonce, json, cipher, tag);
            var body = new byte[nonce.Length + tag.Length + cipher.Length];
            Buffer.BlockCopy(nonce, 0, body, 0, nonce.Length);
            Buffer.BlockCopy(tag, 0, body, nonce.Length, tag.Length);
            Buffer.BlockCopy(cipher, 0, body, nonce.Length + tag.Length, cipher.Length);
            return Prefix(MagicGcm, body);
        }

        // No keyring: plaintext + chmod 600. Said once per process so the fallback is never silent.
        if (Interlocked.Exchange(ref _plaintextFallbackLogged, 1) == 0)
            try { Log?.Invoke("No keyring key was available; the sign-in token was saved unencrypted (file permissions only)."); } catch { }
        return json;
    }

    private static byte[] Prefix(byte[] magic, byte[] body)
    {
        var outBuf = new byte[magic.Length + body.Length];
        Buffer.BlockCopy(magic, 0, outBuf, 0, magic.Length);
        Buffer.BlockCopy(body, 0, outBuf, magic.Length, body.Length);
        return outBuf;
    }

    private static bool Starts(byte[] raw, byte[] magic)
        => raw.Length >= magic.Length && raw.AsSpan(0, magic.Length).SequenceEqual(magic);

    private static byte[] Decrypt(byte[] raw)
    {
        if (Starts(raw, MagicGcm))
        {
            var body = raw.AsSpan(MagicGcm.Length);
            if (body.Length < 28) return raw;                            // truncated; treated as unreadable
            // GetEXISTINGKey, not GetOrCreate: a newly minted key cannot decrypt this file, and creating
            // would cost an extra keyring unlock prompt per restore.
            if (OsKeyring.GetExistingKey() is not { } key) return raw;   // keyring gone/locked -> re-login
            var nonce = body[..12].ToArray();
            var tag = body[12..28].ToArray();
            var cipher = body[28..].ToArray();
            var plain = new byte[cipher.Length];
            using var gcm = new System.Security.Cryptography.AesGcm(key, 16);
            gcm.Decrypt(nonce, cipher, tag, plain);                      // throws on tamper -> caller returns null
            return plain;
        }

        if (!Starts(raw, Magic)) return raw;   // legacy plaintext -- read as-is, re-encrypted on next save
        var enc = raw.AsSpan(Magic.Length).ToArray();
        if (!OperatingSystem.IsWindows()) return enc;   // DPAPI blob is user/machine-bound; unreadable elsewhere -> re-login
        return System.Security.Cryptography.ProtectedData.Unprotect(
            enc, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
    }

    /// <summary>Cheap honesty probe: could the token file plausibly load in THIS process right now? Reads only
    /// the magic; a locked/canceled keyring makes a GRGK1 row read signed OUT rather than claiming a token
    /// it cannot use. Never throws.</summary>
    // ---- Remembered readability (review 09-06) ----
    // The App asks "is this token readable" for every account on every 1 Hz dashboard pass and on every scan
    // progress flush. Each ask is a stat + open + read on the config dir (a USB stick, portable) and, for a
    // keyring-sealed token on Linux, possibly a secret-tool process. Remembered for a few seconds per path;
    // the store's own writes and clears forget it, and hosts can forget it on account events.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (bool Ok, DateTime When)> ReadableCache = new();
    private static readonly TimeSpan ReadableTtl = TimeSpan.FromSeconds(5);

    /// <summary><see cref="LooksReadable"/> with the answer remembered for a few seconds per path.</summary>
    public static bool LooksReadableCached(string path)
    {
        if (ReadableCache.TryGetValue(path, out var hit) && DateTime.UtcNow - hit.When < ReadableTtl) return hit.Ok;
        bool ok = LooksReadable(path);
        ReadableCache[path] = (ok, DateTime.UtcNow);
        return ok;
    }

    /// <summary>Forget the remembered answer for one token file (its bytes changed), or for all.</summary>
    public static void ForgetReadable(string? path = null)
    {
        if (path is null) ReadableCache.Clear(); else ReadableCache.TryRemove(path, out _);
    }

    public static bool LooksReadable(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            var head = new byte[6];
            int n;
            using (var fs = File.OpenRead(path)) n = fs.Read(head, 0, 6);
            if (n <= 0) return false;
            if (n < 6) return head[0] == (byte)'{';
            if (head.AsSpan().SequenceEqual(MagicGcm)) return OsKeyring.GetExistingKey() is not null;
            if (head.AsSpan().SequenceEqual(Magic)) return OperatingSystem.IsWindows();
            return true;   // plaintext (or an unknown future format: optimistic, LoadAsync stays the truth)
        }
        catch { return false; }
    }

    public Task ClearAsync()
    {
        if (File.Exists(_path)) File.Delete(_path);
        ForgetReadable(_path);
        return Task.CompletedTask;
    }
}
