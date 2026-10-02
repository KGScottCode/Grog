// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Grog.Core.Models;

namespace Grog.Core.Manifest;

/// <summary>Loads, holds and atomically saves the one <see cref="LibraryManifest"/> (temp file + rename,
/// same discipline everywhere a reader must never see a half-written file).</summary>
public sealed class JsonManifestStore : IManifestStore
{
    public const string FileName = "grog-manifest.json";
    private const int SyncLogCap = 100;

    // Compact on disk: a 1,500-game manifest indented is megabytes of whitespace written on every save.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },   // readable states in the file
    };

    private readonly string _path;
    private readonly string? _legacyPath;   // old location at the backup root, for one-time migration
    private readonly SemaphoreSlim _gate = new(1, 1);

    // True once the on-disk PRIMARY parsed cleanly (or after a good save). While false -- i.e. we booted
    // from the .bak because the primary was corrupt -- SaveAsync must NOT copy the (bad) primary over the
    // good .bak, or one bad boot would destroy the only surviving copy.
    private bool _primaryTrusted = true;

    /// <summary>Preferred: manifest lives in the .grog config dir; legacy root copy is migrated in.</summary>
    public JsonManifestStore(Storage.GrogPaths paths)
    {
        _path = paths.ManifestPath;
        _legacyPath = Path.Combine(paths.BackupRoot, FileName);
    }

    /// <summary>Back-compat: treats the given directory as both content root and config location
    /// (no .grog split). Used by older callers/tests.</summary>
    public JsonManifestStore(string backupRoot)
    {
        _path = Path.Combine(backupRoot, FileName);
        _legacyPath = null;
    }

    /// <summary>Raised (any thread) when a save gave up because the graph never held still. The host
    /// logs it; the next save retries. Never thrown: most saves are fire-and-forget.</summary>
    public event Action<string>? SaveSkipped;

    public LibraryManifest Current { get; private set; } = new();

    /// <summary>Set when the last load could not read the primary file: one sentence the App and the CLI show,
    /// naming the quarantined copy and what Grog started from. Null after a clean load.</summary>
    public string? RecoveryNote { get; private set; }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            // One-time migration: if there's no manifest in .grog but a legacy one exists at the
            // backup root, move it (and its .bak) into place before loading.
            if (_legacyPath is not null && !File.Exists(_path) && File.Exists(_legacyPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.Move(_legacyPath, _path);
                var legacyBak = _legacyPath + ".bak";
                if (File.Exists(legacyBak)) File.Move(legacyBak, _path + ".bak", overwrite: true);
            }

            RecoveryNote = null;
            // A crash between the two renames of a save leaves no primary but a complete .tmp: finish the save.
            // With a .bak the .tmp is trusted as-is (the second rename was the only step left); without one, a
            // first-ever save may have died mid-write, so the .tmp is adopted only when it parses.
            if (!File.Exists(_path) && File.Exists(_path + ".tmp"))
            {
                bool finish = File.Exists(_path + ".bak")
                              || await TryLoadAsync(_path + ".tmp", ct).ConfigureAwait(false) is not null;
                if (finish)
                    try { File.Move(_path + ".tmp", _path); } catch { /* the .bak fallback below still applies */ }
            }
            if (!File.Exists(_path) && File.Exists(_path + ".bak"))
            {
                // No primary but a backup: the last good save is what the library was; run on it, untrusted.
                var bakOnly = await TryLoadAsync(_path + ".bak", ct).ConfigureAwait(false);
                if (bakOnly is not null)
                {
                    Current = bakOnly;
                    _primaryTrusted = false;
                    RecoveryNote = $"Your library file was missing; Grog started from the backup copy.";
                    using (Gate.Enter()) MigrateSchema();
                    return;
                }
            }
            if (!File.Exists(_path)) { Current = new LibraryManifest(); _primaryTrusted = true; return; }

            // On ANY parse/IO failure fall back to the last-known-good .bak: a corrupt primary must never
            // silently present an empty library (which a later save would make permanent).
            var loaded = await TryLoadAsync(_path, ct).ConfigureAwait(false);
            // A newer build's file is not corrupt and must not be "recovered" from .bak, migrated, or saved over:
            // refuse with a message the host can show (09-08). Both files are left exactly as they are.
            if (loaded is { } newer && newer.SchemaVersion > LibraryManifest.CurrentSchemaVersion)
                throw new ManifestTooNewException(newer.SchemaVersion, LibraryManifest.CurrentSchemaVersion);
            if (loaded is not null)
            {
                Current = loaded;
                _primaryTrusted = true;
            }
            else
            {
                var bak = await TryLoadAsync(_path + ".bak", ct).ConfigureAwait(false);
                if (bak is not null)
                {
                    // Quarantine the corrupt primary for forensics, then run on the recovered copy;
                    // _primaryTrusted = false keeps the good .bak safe until a clean write.
                    TryQuarantine(_path);
                    Current = bak;
                    _primaryTrusted = false;
                    RecoveryNote = $"Your library file could not be read; a copy was kept as {_path}.corrupt and Grog started from the backup copy.";
                }
                else
                {
                    // Neither parses: quarantine the primary first (the next save replaces it, and without the
                    // copy that save would destroy the only remaining bytes), start empty in memory, and stay
                    // untrusted so the next save cannot overwrite the .bak.
                    TryQuarantine(_path);
                    Current = new LibraryManifest();
                    _primaryTrusted = false;
                    RecoveryNote = $"Your library file could not be read; a copy was kept as {_path}.corrupt and Grog started from an empty library.";
                }
            }
            using (Gate.Enter()) MigrateSchema();   // (manifest gate 09-08) the load-time backfills are graph writes
        }
        finally { _gate.Release(); }

    }

    /// <summary>Test seam: the wait between attempts on a file another process holds open.</summary>
    internal static TimeSpan LockRetryGap = TimeSpan.FromMilliseconds(200);
    private const int LockRetries = 3;
    /// <summary>Test seam: how the file is opened, so a sharing violation can be simulated on any OS.</summary>
    internal static Func<string, Stream> OpenForRead = File.OpenRead;

    /// <summary>Null when the file is missing or corrupt (JsonException). A file still locked after three tries
    /// 200 ms apart throws ManifestLockedException: a sharing violation from an indexer or a sync client is not
    /// corruption, and treating it as such quarantined a healthy file and booted from the .bak.</summary>
    private static async Task<LibraryManifest?> TryLoadAsync(string path, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return null;
                await using var stream = OpenForRead(path);
                return await JsonSerializer.DeserializeAsync<LibraryManifest>(stream, JsonOptions, ct).ConfigureAwait(false);
            }
            catch (JsonException) { return null; }   // corrupt/partial -- caller tries the next source
            catch (OperationCanceledException) { throw; }
            catch (IOException ex)
            {
                if (attempt >= LockRetries) throw new ManifestLockedException(path, ex);
                await Task.Delay(LockRetryGap, ct).ConfigureAwait(false);
            }
            catch { return null; }   // unreadable for another reason -- next source
        }
    }

    private static void TryQuarantine(string path)
    {
        try { File.Copy(path, path + ".corrupt", overwrite: true); } catch { /* best-effort forensics */ }
    }

    /// <summary>Forward-only schema migration: v2 = config-dir/roots baseline; v3 = multi-volume (RootId
    /// backfill deferred to BackupLayout); v4 = multi-account fields; v5 = per-file owners; v6 = three-way
    /// extras layout (ExtrasLayout enum derived from the legacy ExtrasInsideGame bool).</summary>
    private void MigrateSchema()
    {
        if (Current.SchemaVersion < 2)
            Current.SchemaVersion = 2;

        if (Current.SchemaVersion < 3)
        {
            // Files keep RootId null until a BackupLayout assigns the real primary id; ResolvePath
            // falls back to PrimaryRootId when RootId is null, so existing backups keep resolving.
            Current.SchemaVersion = 3;
        }

        if (Current.SchemaVersion < 4)
        {
            // v4: multi-account + captured API fields; all additive with safe defaults, nothing to rewrite.
            Current.SchemaVersion = 4;
        }

        // v5: per-file owners, seeded from the item's AccountId ("" = primary). Runs on EVERY load, not
        // gated on version: an older build can re-save a v5 manifest with OwnerIds stripped, and reseeding
        // an already-seeded file is a no-op.
        foreach (var item in Current.Items)
        {
            foreach (var f in item.Files)
                if (f.OwnerIds.Count == 0) f.OwnerIds.Add(item.AccountId);
            foreach (var f in item.OldVersionFiles)
                if (f.OwnerIds.Count == 0) f.OwnerIds.Add(item.AccountId);
        }
        if (Current.SchemaVersion < 5) Current.SchemaVersion = 5;

        // v6: three-way extras layout. Pre-v6 manifests never saw WithGame, so derive from the legacy bool --
        // an existing library keeps its separate-tree shape; only NEW libraries get the WithGame default.
        if (Current.SchemaVersion < 6)
        {
            Current.SetExtrasLayout(Current.ExtrasInsideGame
                ? ExtrasPlacement.SeparateByGame
                : ExtrasPlacement.SeparateByType);
            Current.SchemaVersion = 6;
        }
    }

    /// <summary>Atomic save: temp file + flush + backup + rename.</summary>
    public ManifestGate Gate { get; } = new();

    public Task SaveAsync(CancellationToken ct = default)
        // Everything off the caller's thread: the App fires this from UI handlers, and with the save
        // semaphore free WaitAsync completes synchronously, so the directory create and the whole serialization
        // (a 1,500-game manifest is megabytes) ran on the dispatcher (review 09-06). Task.Run, not Task.Yield:
        // a yield resumes on the same dispatcher. The trim, the legacy mirror and the serialize all run under
        // the manifest gate on the worker (09-08).
        => Task.Run(() => SaveCoreAsync(ct), ct);

    private async Task SaveCoreAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";

            // Serialize under the manifest gate: every mutator holds it, so the graph is quiescent and the
            // snapshot is consistent (no torn "file settled, queue entry still there"). The 09-06 retry loop
            // (serialize a live graph up to eight times, then drop the save) is gone; a "collection was
            // modified" here now means an UNGUARDED writer, which is a bug to find, and is reported, not retried.
            byte[]? bytes = null;
            try
            {
                using (Gate.Enter())
                {
                    TrimSyncLog();
                    // Keep the legacy mirror honest in the file so a downgraded build reads a sane layout.
                    Current.ExtrasInsideGame = Current.ExtrasLayout != ExtrasPlacement.SeparateByType;
                    bytes = JsonSerializer.SerializeToUtf8Bytes(Current, JsonOptions);
                }
            }
            catch (InvalidOperationException ex)
            {
                SaveSkipped?.Invoke($"manifest save skipped: the library was modified outside the manifest gate ({ex.Message})");
                return;
            }

            Interlocked.Increment(ref _saveCount);
            await using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            // Only roll the current primary into .bak when we trust it. After booting from a recovered .bak
            // (primary was corrupt), moving the bad primary here would destroy the only good copy. Two same-volume
            // renames, not a copy: the previous primary becomes the .bak byte for byte without being re-read.
            if (_primaryTrusted && File.Exists(_path))
                File.Move(_path, _path + ".bak", overwrite: true);
            File.Move(tmp, _path, overwrite: true);
            _primaryTrusted = true;   // the primary is now a clean write -- trusted again
        }
        finally { _gate.Release(); }
    }

    /// <summary>Debounced save: marks the manifest dirty and writes once, 250 ms after the first call of a burst,
    /// on a pool thread. Calls inside the window coalesce into that one write. Fire-and-forget by design.</summary>
    public void SaveSoon()
    {
        if (Interlocked.Exchange(ref _dirty, 1) == 1) return;   // a write is already pending for this burst
        _ = Task.Run(async () =>
        {
            await Task.Delay(SaveSoonDelay).ConfigureAwait(false);
            Volatile.Write(ref _dirty, 0);   // the next SaveSoon opens a new burst
            try { await SaveCoreAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { SaveSkipped?.Invoke($"manifest save skipped: {ex.Message}"); }
        });
    }
    private int _dirty;
    /// <summary>Test seam: the debounce window of <see cref="SaveSoon"/>.</summary>
    internal TimeSpan SaveSoonDelay { get; set; } = TimeSpan.FromMilliseconds(250);
    /// <summary>Test seam: how many times the file was written.</summary>
    internal int SaveCount => Volatile.Read(ref _saveCount);
    private int _saveCount;

    private void TrimSyncLog()
    {
        if (Current.SyncLog.Count > SyncLogCap)
            Current.SyncLog.RemoveRange(SyncLogCap, Current.SyncLog.Count - SyncLogCap);
    }
}

/// <summary>Another process still holds the manifest open after the read retries: not corruption, so nothing
/// is quarantined or recovered; the host reports it and the user retries once the other program lets go.</summary>
public sealed class ManifestLockedException : Exception
{
    public string Path { get; }
    public ManifestLockedException(string path, Exception inner)
        : base($"Your library file is in use by another program ({inner.Message}). Close it and try again.", inner)
    { Path = path; }
}

/// <summary>The manifest on disk was written by a newer Grog than this one.</summary>
public sealed class ManifestTooNewException : Exception
{
    public int FileVersion { get; }
    public int SupportedVersion { get; }
    public ManifestTooNewException(int fileVersion, int supportedVersion)
        : base($"This library file was written by a newer version of Grog (format {fileVersion}; this build reads up to {supportedVersion}). Update Grog to open it.")
    { FileVersion = fileVersion; SupportedVersion = supportedVersion; }
}
