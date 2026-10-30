// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Grog.Core.Volumes;

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

    public void Save(ReorgJob job)
    {
        var tmp = _path + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(job, Json);
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
        }
        if (TryLoad(_path) is not null)
        {
            try
            {
                File.Copy(_path, _path + ".bak", overwrite: true);
                File.SetAttributes(_path + ".bak", FileAttributes.Normal);   // a read-only primary must not breed a read-only backup
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best-effort: the primary still lands */ }
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

    // A job written by a newer Grog is not ours to resume: leave it on disk untouched.
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
        try { if (File.Exists(_path)) File.Delete(_path); } catch { /* best-effort */ }
        try { if (File.Exists(_path + ".bak")) File.Delete(_path + ".bak"); } catch { /* best-effort */ }
    }
}
