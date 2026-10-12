// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grog.Core.Manifest;
using Grog.Core.Models;

namespace Grog.Core.Verify;

public sealed class VerifyService
{
    private readonly IManifestStore _manifest;
    public string BackupRoot { get; }
    public Volumes.IBackupLayout? Layout { get; set; }

    public event Action<VerifyProgress>? Progress;

    public Func<GameFile, CancellationToken, Task<string?>>? FetchCurrentServerMd5 { get; set; }

    public bool FetchMissingChecksums { get; set; }

    public VerifyService(IManifestStore manifest, string backupRoot)
    {
        _manifest = manifest;
        BackupRoot = backupRoot;
    }

    public Task<VerifyResult> RunAsync(bool full, CancellationToken ct = default)
        => RunAsync(full ? VerifyMode.FullRehash : VerifyMode.SizeOnly, ct);

    public async Task<VerifyResult> RunAsync(VerifyMode mode, CancellationToken ct = default)
    {
        var filesWithLocal = _manifest.Current.Items
            .SelectMany(i => i.Files)
            .Where(f => !string.IsNullOrEmpty(f.LocalRelativePath))
            .ToList();

        var result = await CheckFilesAsync(filesWithLocal, mode, ct);

        // Roll up game statuses and save.
        using (_manifest.Gate.Enter())   // 
            foreach (var item in _manifest.Current.Items)
                Sync.LibrarySyncService.RecomputeStatus(item);
        await _manifest.SaveAsync(ct);
        return result;
    }

    public async Task<VerifyResult> RunForItemsAsync(IEnumerable<LibraryItem> items, VerifyMode mode, CancellationToken ct = default)
    {
        var itemList = items.ToList();
        var files = itemList
            .SelectMany(i => i.Files)
            .Where(f => !string.IsNullOrEmpty(f.LocalRelativePath))
            .ToList();

        var result = await CheckFilesAsync(files, mode, ct);

        using (_manifest.Gate.Enter())   // 
            foreach (var item in itemList)
                Sync.LibrarySyncService.RecomputeStatus(item);
        await _manifest.SaveAsync(ct);
        return result;
    }

    public async Task<VerifyResult> ConfirmAsync(IEnumerable<LibraryItem> items, CancellationToken ct = default)
    {
        var itemList = items.ToList();
        var files = itemList
            .SelectMany(i => i.Files)
            .Where(f => !string.IsNullOrEmpty(f.LocalRelativePath))
            .ToList();

        var result = await CheckFilesAsync(files, VerifyMode.SizeOnly, ct);

        using (_manifest.Gate.Enter())   // 
            foreach (var item in itemList)
                Sync.LibrarySyncService.RecomputeStatus(item);
        await _manifest.SaveAsync(ct);
        return result;
    }

    public Task<VerifyResult> ConfirmFilesAsync(IEnumerable<GameFile> files,
        IEnumerable<LibraryItem> parents, CancellationToken ct = default)
        => RunForFilesAsync(files, parents, VerifyMode.SizeOnly, ct);

    public async Task<VerifyResult> RunForFilesAsync(IEnumerable<GameFile> files,
        IEnumerable<LibraryItem> parents, VerifyMode mode, CancellationToken ct = default)
    {
        var fileList = files.Where(f => !string.IsNullOrEmpty(f.LocalRelativePath)).ToList();
        var result = await CheckFilesAsync(fileList, mode, ct);
        using (_manifest.Gate.Enter())   // 
            foreach (var item in parents)
                Sync.LibrarySyncService.RecomputeStatus(item);
        await _manifest.SaveAsync(ct);
        return result;
    }

    private void Gated(Action write)
    {
        using (_manifest.Gate.Enter()) write();
    }

    private async Task<VerifyResult> CheckFilesAsync(List<GameFile> files, VerifyMode mode, CancellationToken ct)
    {
        var result = new VerifyResult();
        int done = 0;
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();

            if (f.State == FileState.NotBackedUp)
            {
                Progress?.Invoke(new VerifyProgress(++done, files.Count));
                continue;
            }

            string? path;
            if (Layout is not null)
            {
                if (!Layout.IsOnline(_manifest.Current.EffectiveRootId(f)))
                {
                    result.OfflineSkipped++;
                    Progress?.Invoke(new VerifyProgress(++done, files.Count));
                    continue;
                }
                path = Layout.ResolvePath(f);
            }
            else
            {
                var native = f.LocalRelativePath!
                    .Replace('\\', Path.DirectorySeparatorChar)
                    .Replace('/', Path.DirectorySeparatorChar);
                path = Path.Combine(BackupRoot, native);
            }

            if (path is null || !File.Exists(path))
            {
                bool flagged = false;
                Gated(() => { if (!string.IsNullOrEmpty(f.LocalRelativePath)) { f.State = FileState.Missing; flagged = true; } });
                if (flagged) result.Missing++;
            }
            else
            {
                var size = new FileInfo(path).Length;
                bool sizeOk;
                if (f.LocalSizeBytes is { } exact)
                {
                    sizeOk = size == exact;
                }
                else if (f.ExpectedSizeBytes is { } est && est > 0)
                {
                    var tolerance = Math.Max(1024L * 1024, (long)(est * 0.02)); // 2% or 1 MB
                    sizeOk = Math.Abs(size - est) <= tolerance;
                }
                else
                {
                    sizeOk = true; // nothing to compare against
                }
                if (!sizeOk)
                {
                    Gated(() => f.State = FileState.Corrupt);
                    result.Corrupt++;
                }
                // No checksum on record
                else if (FetchMissingChecksums && mode != VerifyMode.SizeOnly && string.IsNullOrEmpty(f.ExpectedMd5)
                         && f.State is not FileState.Verified and not FileState.Corrupt && FetchCurrentServerMd5 is not null
                         && await FetchCurrentServerMd5(f, ct) is { Length: > 0 } server)
                {
                    var md5 = await Hashing.Md5Async(path, ct);
                    if (string.Equals(md5, server, StringComparison.OrdinalIgnoreCase))
                    {
                        Gated(() =>
                        {
                            if (string.IsNullOrEmpty(f.ExpectedMd5)) f.ExpectedMd5 = server;
                            f.State = FileState.Verified;
                            f.LastVerifiedAt = DateTimeOffset.UtcNow;
                            f.LocalSizeBytes = size;
                        });
                        result.Verified++;
                    }
                    else
                    {
                        Gated(() =>
                        {
                            if (f.State is not FileState.Verified and not FileState.Outdated) f.State = FileState.Present;
                            f.LocalSizeBytes = size;
                        });
                        result.SizeOnlyOk++;
                        result.DiffersFromGog++;
                    }
                }
                else if (!string.IsNullOrEmpty(f.ExpectedMd5) && mode switch
                         {
                             VerifyMode.FullRehash => true,
                             VerifyMode.Unverified => f.State != FileState.Verified,
                             _ => false,
                         })
                {
                    var md5 = await Hashing.Md5Async(path, ct);
                    if (string.Equals(md5, f.ExpectedMd5, StringComparison.OrdinalIgnoreCase))
                    {
                        Gated(() =>
                        {
                            f.State = FileState.Verified;
                            f.LastVerifiedAt = DateTimeOffset.UtcNow;
                            f.LocalSizeBytes = size;
                        });
                        result.Verified++;
                    }
                    else
                    {
                        var current = FetchCurrentServerMd5 is null ? null
                            : await FetchCurrentServerMd5(f, ct);
                        if (current is not null &&
                            !string.Equals(current, f.ExpectedMd5, StringComparison.OrdinalIgnoreCase))
                        {
                            Gated(() => f.State = FileState.Outdated);
                            result.Updated++;
                        }
                        else
                        {
                            Gated(() => f.State = FileState.Corrupt);   
                            result.Corrupt++;
                        }
                    }
                }
                else if (f.State == FileState.Corrupt)
                {
                    // Size OK but already flagged Corrupt
                    result.Corrupt++;
                }
                else
                {
                    // Size OK, no hash (or fast mode)
                    Gated(() =>
                    {
                        if (f.State is not FileState.Verified and not FileState.Outdated)
                            f.State = FileState.Present;
                        f.LocalSizeBytes = size;
                    });
                    result.SizeOnlyOk++;

                }
            }

            Progress?.Invoke(new VerifyProgress(++done, files.Count));
        }

        result.Checked = files.Count;
        return result;
    }
}

public enum VerifyMode
{
    SizeOnly,
    Unverified,
    FullRehash,
}

public sealed record VerifyProgress(int Done, int Total);

public sealed class VerifyResult
{
    public int Checked { get; set; }
    public int Verified { get; set; }
    public int SizeOnlyOk { get; set; }
    public int Corrupt { get; set; }
    public int Updated { get; set; }
    public int Missing { get; set; }
    public int DiffersFromGog { get; set; }
    public int OfflineSkipped { get; set; }
}
