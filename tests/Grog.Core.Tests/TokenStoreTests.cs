// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Grog.Core.Auth;
using Grog.Core.Platform;
using Grog.Core.Tests.Framework;

namespace Grog.Core.Tests;

/// <summary>
/// The token store is the one file whose failure mode is a SILENT LOGOUT: an unreadable store looks exactly
/// like "never signed in". These tests exist because the encryption-at-rest change added a second on-disk
/// format, and a format that cannot be read by the build that follows it costs every user their session.
///
/// <para>These run on whatever platform the suite runs on. That is the point of writing them against the
/// public contract rather than against DPAPI or a keyring: the round-trip and the legacy-read must hold on
/// all three, and any platform where they do not is a platform Grog must not ship on. LINUX AND macOS HAVE
/// NOT BEEN WALKED BY HAND YET -- these tests prove the format logic, not the keyring integration.</para>
/// </summary>
[Trait("auth")]
public sealed class TokenStoreTests
{
    private string _dir = "";
    private string Path_ => System.IO.Path.Combine(_dir, "tokens.json");

    [Setup]
    public void Setup()
    {
        _dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "grog-tok-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    [Teardown]
    public void Teardown()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static AuthSession Sample() => new(
        AccessToken: "acc-123",
        RefreshToken: "ref-456",
        ExpiresAt: DateTimeOffset.UtcNow.AddHours(1),
        UserId: "user-789")
    { ObtainedAt = DateTimeOffset.UtcNow };

    [Test]
    public async Task Round_trips_a_session_on_this_platform()
    {
        var store = new FileTokenStore(Path_);
        await store.SaveAsync(Sample());
        var back = await store.LoadAsync();

        Assert.True(back is not null, "a saved session loads again");
        Assert.Equal("ref-456", back!.RefreshToken, "the refresh token survives the round trip");
        Assert.Equal("user-789", back.UserId, "the user id survives the round trip");
    }

    [Test]
    public async Task Never_leaves_the_refresh_token_readable_in_the_file_when_protected()
    {
        // The whole point of the change. On a platform with no keyring reachable this is EXPECTED to be
        // plaintext -- and FileTokenStore.Protection must be saying so, which is the assertion that keeps the
        // UI copy honest. What must never happen is "protection claims encryption" AND "token is readable".
        var store = new FileTokenStore(Path_);
        await store.SaveAsync(Sample());
        var raw = await File.ReadAllBytesAsync(Path_);
        bool readable = Encoding.UTF8.GetString(raw).Contains("ref-456", StringComparison.Ordinal);

        if (store.FileProtection == TokenProtection.FilePermissionsOnly)
            Assert.True(readable, "with no keyring the file is plaintext, and Protection admits it");
        else
            Assert.True(!readable, "when Protection claims encryption the token must not be in the clear");
    }

    [Test]
    public async Task Reads_a_legacy_plaintext_store_and_upgrades_it_on_save()
    {
        // A store written by a build before encryption existed. It must still load -- otherwise upgrading
        // Grog logs everyone out -- and the next save must move it to the current format.
        var json = System.Text.Json.JsonSerializer.Serialize(Sample());
        await File.WriteAllTextAsync(Path_, json);

        var store = new FileTokenStore(Path_);
        var loaded = await store.LoadAsync();
        Assert.True(loaded is not null, "a legacy plaintext store still loads");
        Assert.Equal("ref-456", loaded!.RefreshToken, "and its contents are intact");

        await store.SaveAsync(loaded);
        var reloaded = await store.LoadAsync();
        Assert.True(reloaded is not null, "the upgraded store loads back");
        Assert.Equal("ref-456", reloaded!.RefreshToken, "with the same contents");
    }

    [Test]
    public async Task A_tampered_or_corrupt_store_reads_as_signed_out_rather_than_throwing()
    {
        var store = new FileTokenStore(Path_);
        await store.SaveAsync(Sample());
        var raw = await File.ReadAllBytesAsync(Path_);
        raw[^1] ^= 0xFF;                       // flip a bit in the last byte
        await File.WriteAllBytesAsync(Path_, raw);

        var back = await store.LoadAsync();
        Assert.True(back is null || back.RefreshToken != "ref-456",
            "a damaged store must never hand back a half-decoded credential");
    }

    [Test]
    public async Task Clear_removes_the_store()
    {
        var store = new FileTokenStore(Path_);
        await store.SaveAsync(Sample());
        await store.ClearAsync();
        Assert.True(!File.Exists(Path_), "clearing deletes the file");
        Assert.True(await store.LoadAsync() is null, "and loading afterwards is signed out");
    }

    [Test]
    public async Task Plaintext_preference_forces_plaintext_and_reports_it_honestly()
    {
        // The scheduled-tasks opt-out: with the marker set, every platform (including Windows, where the
        // toggle is hidden but the mechanism must still hold) writes plaintext and Protection admits it --
        // the honesty rule is "protection claims encryption" and "token readable" never coexist, and this
        // is the one state where readable is chosen ON PURPOSE.
        FileTokenStore.MarkerPathOverride = System.IO.Path.Combine(_dir, "token-plaintext");
        try
        {
            FileTokenStore.SetPlainTextPreferred(true);
            Assert.True(FileTokenStore.PlainTextPreferred, "the marker file makes the preference true");
            Assert.Equal(TokenProtection.FilePermissionsOnly, FileTokenStore.Protection,
                "the preference overrides every platform's encryption claim");

            var store = new FileTokenStore(Path_);
            await store.SaveAsync(Sample());
            var raw = await File.ReadAllBytesAsync(Path_);
            Assert.True(raw.Length > 0 && raw[0] == (byte)'{', "the file on disk is plain JSON");
            var back = await store.LoadAsync();
            Assert.Equal("ref-456", back!.RefreshToken, "and it round-trips");

            FileTokenStore.SetPlainTextPreferred(false);
            Assert.True(!FileTokenStore.PlainTextPreferred, "turning it off removes the marker");
        }
        finally
        {
            FileTokenStore.SetPlainTextPreferred(false);
            FileTokenStore.MarkerPathOverride = null;
        }
    }

    [Test]
    public async Task An_encrypted_store_still_loads_after_the_plaintext_preference_turns_on()
    {
        // Flipping the toggle must not orphan the token that already exists: Decrypt accepts every format
        // regardless of the preference, so the session survives until the next save rewrites it plaintext.
        var store = new FileTokenStore(Path_);
        await store.SaveAsync(Sample());                     // whatever this platform writes
        FileTokenStore.MarkerPathOverride = System.IO.Path.Combine(_dir, "token-plaintext");
        try
        {
            FileTokenStore.SetPlainTextPreferred(true);
            var back = await store.LoadAsync();
            Assert.Equal("ref-456", back!.RefreshToken, "the pre-toggle store still reads");
            await store.SaveAsync(back);
            var raw = await File.ReadAllBytesAsync(Path_);
            Assert.True(raw[0] == (byte)'{', "and the next save rewrites it plaintext");
        }
        finally
        {
            FileTokenStore.SetPlainTextPreferred(false);
            FileTokenStore.MarkerPathOverride = null;
        }
    }

    [Test]
    public void Protection_reason_names_the_opt_out_when_the_marker_is_set()
    {
        // The reason must name the CAUSE, not just the state: the UI's opt-out sentence ("so scheduled
        // CLI runs can read it") is only true when the user chose it, and this is the choice.
        FileTokenStore.MarkerPathOverride = System.IO.Path.Combine(_dir, "token-plaintext");
        try
        {
            FileTokenStore.SetPlainTextPreferred(true);
            Assert.Equal(TokenProtectionReason.OptedOut, FileTokenStore.ProtectionReason,
                "the marker file is the one cause that is the user's own choice");
        }
        finally
        {
            FileTokenStore.SetPlainTextPreferred(false);
            FileTokenStore.MarkerPathOverride = null;
        }
    }

    [Test]
    public async Task Looks_readable_rejects_an_orphaned_keyring_file()
    {
        // The B1 predicate (Linux walk 2026-08-23): a GRGK1 file whose key is not in the keyring EXISTS
        // but cannot decrypt. Judging it by existence read "already connected" and burned a real GOG
        // login; every connected-state and adopt decision must use LooksReadable instead.
        Assert.SkipUnless(OsKeyring.GetExistingKey() is null,
            "this machine can decrypt GRGK1; the orphan case needs a keyless environment");

        var orphan = System.IO.Path.Combine(_dir, "orphan.json");
        var bytes = new byte[9];
        Encoding.ASCII.GetBytes("GRGK1\n").CopyTo(bytes, 0);
        bytes[6] = 1; bytes[7] = 2; bytes[8] = 3;
        await File.WriteAllBytesAsync(orphan, bytes);
        Assert.True(File.Exists(orphan), "the orphan exists on disk");
        Assert.True(!FileTokenStore.LooksReadable(orphan), "but it must not read as a usable sign-in");

        var plain = System.IO.Path.Combine(_dir, "plain.json");
        await File.WriteAllTextAsync(plain, "{}");
        Assert.True(FileTokenStore.LooksReadable(plain), "plaintext stays readable everywhere");
    }

    [Test]
    public async Task A_keyring_that_yields_no_key_writes_plaintext_and_Protection_says_so()
    {
        // The keyring can answer null for 60 s after a blip. The save then falls back to plaintext; that
        // must be LOGGED, and Protection must read the file's real header rather than the keyring's mood.
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "Windows encrypts with DPAPI and never consults a keyring");
        var wasSource = FileTokenStore.KeySource;
        var wasLog = FileTokenStore.Log;
        var lines = new System.Collections.Generic.List<string>();
        FileTokenStore.KeySource = () => null;
        FileTokenStore.Log = lines.Add;
        try
        {
            var store = new FileTokenStore(Path_);
            await store.SaveAsync(Sample());
            var raw = await File.ReadAllBytesAsync(Path_);
            Assert.True(raw.Length > 0 && raw[0] == (byte)'{', "no key: the file on disk is plain JSON");
            Assert.Equal(TokenProtection.FilePermissionsOnly, store.FileProtection,
                "Protection reads the file's header, not the keyring's claim");
            Assert.Equal(TokenProtection.FilePermissionsOnly, FileTokenStore.ProtectionOf(Path_),
                "the static readout agrees for the same path");
            Assert.True(FileTokenStore.PlaintextFallbackHappened, "the fallback is recorded");
            Assert.True(lines.Count <= 1, "logged at most once per process (an earlier test may have spent it)");
            if (lines.Count == 1) Assert.Contains("unencrypted", lines[0], "the line names the fallback");
        }
        finally { FileTokenStore.KeySource = wasSource; FileTokenStore.Log = wasLog; }
    }

    [Test]
    public async Task Protection_reads_an_encrypted_header_as_encrypted_even_when_the_keyring_is_absent()
    {
        // The other half of the honesty rule: a GRGK1 file IS encrypted whatever the keyring says right now.
        Assert.SkipUnless(!FileTokenStore.PlainTextPreferred, "precondition: no plaintext marker");
        var bytes = new byte[40];
        Encoding.ASCII.GetBytes("GRGK1\n").CopyTo(bytes, 0);
        await File.WriteAllBytesAsync(Path_, bytes);
        var p = FileTokenStore.ProtectionOf(Path_);
        Assert.True(p == TokenProtection.LinuxKeyring || p == TokenProtection.MacKeychain,
            $"a keyring-sealed header reads as keyring protection, got {p}");
    }

    [Test]
    public void Protection_reason_agrees_with_protection()
    {
        // The two readouts must never disagree: Encrypted iff Protection claims encryption, a concrete
        // cause iff it does not. Platform-agnostic on purpose -- whatever this machine's answer is, the
        // pair must be consistent, or the card's badge and body describe two different machines.
        var p = FileTokenStore.Protection;
        var r = FileTokenStore.ProtectionReason;
        if (p == TokenProtection.FilePermissionsOnly)
            Assert.True(r != TokenProtectionReason.Encrypted,
                "an unencrypted store must present a concrete cause, not 'Encrypted'");
        else
            Assert.Equal(TokenProtectionReason.Encrypted, r,
                "an encrypted store has nothing to explain");
    }

    [Test]
    public void Protection_reason_without_marker_is_keyring_dependent_not_opt_out()
    {
        // With no marker, whatever else the reason is it must NOT be OptedOut -- this is exactly the
        // false sentence the old card showed a keyring-less Linux user.
        Assert.True(!FileTokenStore.PlainTextPreferred, "precondition: no marker set in this environment");
        Assert.True(FileTokenStore.ProtectionReason != TokenProtectionReason.OptedOut,
            "no marker means the opt-out explanation may never be shown");
    }

    [Test]
    public async Task Concurrent_saves_to_one_path_leave_one_valid_file_and_no_tmp()
    {
        var a = new FileTokenStore(Path_);
        var b = new FileTokenStore(Path_);
        var tasks = new System.Collections.Generic.List<Task>();
        for (int i = 0; i < 20; i++)
        {
            tasks.Add(Task.Run(() => a.SaveAsync(Sample())));
            tasks.Add(Task.Run(() => b.SaveAsync(Sample())));
        }
        await Task.WhenAll(tasks);

        var back = await new FileTokenStore(Path_).LoadAsync();
        Assert.True(back is not null && back.RefreshToken == "ref-456", "the surviving file is a whole, readable session");
        var leftovers = Directory.GetFiles(_dir, "*.tmp");
        Assert.Equal(0, leftovers.Length, "no temp file is left behind: " + string.Join(", ", leftovers));
        Assert.Equal(1, Directory.GetFiles(_dir).Length, "exactly one file remains");
    }
}
