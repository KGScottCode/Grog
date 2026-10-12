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

public sealed class JsonManifestStore : IManifestStore
{
    public const string FileName = "grog-manifest.json";
    private const int SyncLogCap = 100;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },   // readable states in the file
    };

    private readonly string _path;
    private readonly string? _legacyPath;   // old location at the backup root, for one-time migration
    private readonly SemaphoreSlim _gate = new(1, 1);

    private bool _primaryTrusted = true;

    public JsonManifestStore(Storage.GrogPaths paths)
    {
        _path = paths.ManifestPath;
        _legacyPath = Path.Combine(paths.BackupRoot, FileName);
    }

    public JsonManifestStore(string backupRoot)
    {
        _path = Path.Combine(backupRoot, FileName);
        _legacyPath = null;
    }

    public event Action<string>? SaveSkipped;

    public LibraryManifest Current { get; private set; } = new();

    public string? RecoveryNote { get; private set; }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            // One-time migration
            if (_legacyPath is not null && !File.Exists(_path) && File.Exists(_legacyPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.Move(_legacyPath, _path);
                var legacyBak = _legacyPath + ".bak";
                if (File.Exists(legacyBak)) File.Move(legacyBak, _path + ".bak", overwrite: true);
            }

            RecoveryNote = null;
            if (!File.Exists(_path) && File.Exists(_path + ".tmp"))
            {
                bool finish = File.Exists(_path + ".bak")
                              || await TryLoadAsync(_path + ".tmp", ct).ConfigureAwait(false) is not null;
                if (finish)
                    try { File.Move(_path + ".tmp", _path); } catch { /* the .bak fallback below still applies */ }
            }
            if (!File.Exists(_path) && File.Exists(_path + ".bak"))
            {
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

            var loaded = await TryLoadAsync(_path, ct).ConfigureAwait(false);
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
                    TryQuarantine(_path);
                    Current = bak;
                    _primaryTrusted = false;
                    RecoveryNote = $"Your library file could not be read; a copy was kept as {_path}.corrupt and Grog started from the backup copy.";
                }
                else
                {
                    // Neither parses
                    TryQuarantine(_path);
                    Current = new LibraryManifest();
                    _primaryTrusted = false;
                    RecoveryNote = $"Your library file could not be read; a copy was kept as {_path}.corrupt and Grog started from an empty library.";
                }
            }
            using (Gate.Enter()) MigrateSchema();   // the load-time backfills are graph writes
        }
        finally { _gate.Release(); }

    }

    internal static TimeSpan LockRetryGap = TimeSpan.FromMilliseconds(200);
    private const int LockRetries = 3;
    internal static Func<string, Stream> OpenForRead = File.OpenRead;

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

    private void MigrateSchema()
    {
        if (Current.SchemaVersion < 2)
            Current.SchemaVersion = 2;

        if (Current.SchemaVersion < 3)
        {
            Current.SchemaVersion = 3;
        }

        if (Current.SchemaVersion < 4)
        {
            Current.SchemaVersion = 4;
        }

        foreach (var item in Current.Items)
        {
            foreach (var f in item.Files)
                if (f.OwnerIds.Count == 0) f.OwnerIds.Add(item.AccountId);
            foreach (var f in item.OldVersionFiles)
                if (f.OwnerIds.Count == 0) f.OwnerIds.Add(item.AccountId);
        }
        if (Current.SchemaVersion < 5) Current.SchemaVersion = 5;

        if (Current.SchemaVersion < 6)
        {
            Current.SetExtrasLayout(Current.ExtrasInsideGame
                ? ExtrasPlacement.SeparateByGame
                : ExtrasPlacement.SeparateByType);
            Current.SchemaVersion = 6;
        }
    }

    public ManifestGate Gate { get; } = new();

    public Task SaveAsync(CancellationToken ct = default)
        => Task.Run(() => SaveCoreAsync(ct), ct);

    private async Task SaveCoreAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";

            byte[]? bytes = null;
            try
            {
                using (Gate.Enter())
                {
                    TrimSyncLog();
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

            if (_primaryTrusted && File.Exists(_path))
                File.Move(_path, _path + ".bak", overwrite: true);
            File.Move(tmp, _path, overwrite: true);
            _primaryTrusted = true;   // the primary is now a clean write -- trusted again
        }
        finally { _gate.Release(); }
    }

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
    internal TimeSpan SaveSoonDelay { get; set; } = TimeSpan.FromMilliseconds(250);
    internal int SaveCount => Volatile.Read(ref _saveCount);
    private int _saveCount;

    private void TrimSyncLog()
    {
        if (Current.SyncLog.Count > SyncLogCap)
            Current.SyncLog.RemoveRange(SyncLogCap, Current.SyncLog.Count - SyncLogCap);
    }
}

public sealed class ManifestLockedException : Exception
{
    public string Path { get; }
    public ManifestLockedException(string path, Exception inner)
        : base($"Your library file is in use by another program ({inner.Message}). Close it and try again.", inner)
    { Path = path; }
}

public sealed class ManifestTooNewException : Exception
{
    public int FileVersion { get; }
    public int SupportedVersion { get; }
    public ManifestTooNewException(int fileVersion, int supportedVersion)
        : base($"This library file was written by a newer version of Grog (format {fileVersion}; this build reads up to {supportedVersion}). Update Grog to open it.")
    { FileVersion = fileVersion; SupportedVersion = supportedVersion; }
}
