// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Grog.Core.Volumes;

/// <summary>Persists the reorg journal to <c>&lt;configDir&gt;/reorg.json</c>. Lives in the profile
/// config dir (not the backup drive) so it survives even if the drive being reorganized goes offline
/// mid-move. Writes are atomic (temp flushed to disk, previous kept as .bak, then replace) so a crash never
/// leaves a half-written journal, and a corrupt one falls back to the .bak on load.</summary>
public sealed class ReorgJournalStore
{
    public const string FileName = "reorg.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;

    public ReorgJournalStore(string configDir) => _path = System.IO.Path.Combine(configDir, FileName);

    public bool Exists => File.Exists(_path);

    /// <summary>Atomically write the journal: temp file flushed to disk, the previous primary kept as .bak,
    /// then replace. A READ-ONLY destination makes the replace throw UnauthorizedAccessException (owner-hit
    /// 2026-08-30: a reorg.json carrying the read-only attribute crashed the run at Save) -- clear the
    /// attribute and retry once before giving up.</summary>
    public void Save(ReorgJob job)
    {
        var tmp = _path + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(job, Json);
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
        }
        // Decided from the disk, not an instance flag: the App loads and saves through different instances. A
        // primary that does not parse is corrupt, and rolling it into .bak would destroy the only good copy.
        if (TryLoad(_path) is not null)
        {
            try { File.Copy(_path, _path + ".bak", overwrite: true); } catch { /* best-effort: the primary still lands */ }
        }
        try
        {
            File.Move(tmp, _path, overwrite: true);
        }
        catch (UnauthorizedAccessException)
        {
            if (!File.Exists(_path)) throw;   // not the read-only case: ACLs etc., nothing we can fix
            File.SetAttributes(_path, FileAttributes.Normal);
            File.Move(tmp, _path, overwrite: true);
        }
    }

    /// <summary>Load a pending journal, or null if none / unreadable. A primary that fails to parse is
    /// quarantined as .corrupt and the last-known-good .bak is used instead.</summary>
    public ReorgJob? Load()
    {
        if (!File.Exists(_path)) return null;
        var job = TryLoad(_path);
        if (job is not null) return TooNew(job) ? null : job;

        var bak = TryLoad(_path + ".bak");
        if (bak is null) return null;
        try { File.Copy(_path, _path + ".corrupt", overwrite: true); } catch { /* best-effort forensics */ }
        return TooNew(bak) ? null : bak;
    }

    // A job written by a newer Grog is not ours to resume: leave it on disk untouched (09-08).
    private static bool TooNew(ReorgJob j) => j.Schema > ReorgJob.CurrentSchema;

    private static ReorgJob? TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<ReorgJob>(File.ReadAllText(path), Json);
        }
        catch { return null; }
    }

    public void Delete()
    {
        // The .bak goes too: a stale one must never resurface as a later job's fallback.
        try { if (File.Exists(_path)) File.Delete(_path); } catch { /* best-effort */ }
        try { if (File.Exists(_path + ".bak")) File.Delete(_path + ".bak"); } catch { /* best-effort */ }
    }
}
