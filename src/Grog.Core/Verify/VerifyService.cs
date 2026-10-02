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

/// <summary>
/// Checks manifest-recorded files against disk: size-only (missing/truncated) or MD5 (silent corruption).
/// Updates each file's State and returns a summary. Never downloads; purely local.
/// </summary>
public sealed class VerifyService
{
    private readonly IManifestStore _manifest;
    public string BackupRoot { get; }
    /// <summary>Optional multi-volume layout; when set, paths resolve per-root and offline roots are skipped.</summary>
    public Volumes.IBackupLayout? Layout { get; set; }

    public event Action<VerifyProgress>? Progress;

    /// <summary>Optional bridge to GOG's current checksum for a file slot (null = offline / not wired).
    /// When set, a hash mismatch where GOG now ships a different hash is a silent repack (Outdated, not
    /// Corrupt); when null, every mismatch is Corrupt (the safe default).</summary>
    public Func<GameFile, CancellationToken, Task<string?>>? FetchCurrentServerMd5 { get; set; }

    /// <summary>(09-25, owner) A hashing pass also asks GOG for the checksum of a file that has none on record
    /// (an adopted file) through <see cref="FetchCurrentServerMd5"/>, and hashes it. A match stores the checksum
    /// and reads Verified. A mismatch changes NOTHING on the record (state, checksum): the copy may be an older
    /// build or damaged, and marking it Outdated or Corrupt would queue a download that replaces bytes the user
    /// brought. It is counted in <see cref="VerifyResult.DiffersFromGog"/> and the host names it; the user
    /// decides. Inform, never choose.</summary>
    public bool FetchMissingChecksums { get; set; }

    public VerifyService(IManifestStore manifest, string backupRoot)
    {
        _manifest = manifest;
        BackupRoot = backupRoot;
    }

    /// <summary>Back-compat: false = size only, true = MD5 re-hash everything.</summary>
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
        using (_manifest.Gate.Enter())   // (manifest gate 09-08)
            foreach (var item in _manifest.Current.Items)
                Sync.LibrarySyncService.RecomputeStatus(item);
        await _manifest.SaveAsync(ct);
        return result;
    }

    /// <summary>Verifies only the given items' files, rolling up and persisting just those products. Backs the
    /// per-game "Rescan &amp; Verify" action.</summary>
    public async Task<VerifyResult> RunForItemsAsync(IEnumerable<LibraryItem> items, VerifyMode mode, CancellationToken ct = default)
    {
        var itemList = items.ToList();
        var files = itemList
            .SelectMany(i => i.Files)
            .Where(f => !string.IsNullOrEmpty(f.LocalRelativePath))
            .ToList();

        var result = await CheckFilesAsync(files, mode, ct);

        using (_manifest.Gate.Enter())   // (manifest gate 09-08)
            foreach (var item in itemList)
                Sync.LibrarySyncService.RecomputeStatus(item);
        await _manifest.SaveAsync(ct);
        return result;
    }

    /// <summary>Confirms just the given items against disk (presence + size); MD5 was checked at download time,
    /// this only reconciles the UI with disk reality. Updates States, rolls up, persists.</summary>
    public async Task<VerifyResult> ConfirmAsync(IEnumerable<LibraryItem> items, CancellationToken ct = default)
    {
        var itemList = items.ToList();
        var files = itemList
            .SelectMany(i => i.Files)
            .Where(f => !string.IsNullOrEmpty(f.LocalRelativePath))
            .ToList();

        var result = await CheckFilesAsync(files, VerifyMode.SizeOnly, ct);

        using (_manifest.Gate.Enter())   // (manifest gate 09-08)
            foreach (var item in itemList)
                Sync.LibrarySyncService.RecomputeStatus(item);
        await _manifest.SaveAsync(ct);
        return result;
    }

    /// <summary>Confirms a specific set of files against disk (size mode); rolls up only the given parents.</summary>
    public Task<VerifyResult> ConfirmFilesAsync(IEnumerable<GameFile> files,
        IEnumerable<LibraryItem> parents, CancellationToken ct = default)
        => RunForFilesAsync(files, parents, VerifyMode.SizeOnly, ct);

    /// <summary>(09-19) The same, in the mode asked: <see cref="VerifyMode.FullRehash"/> backs the Storage file
    /// row's "Rescan &amp; verify", which must read the bytes like the game row's does.</summary>
    public async Task<VerifyResult> RunForFilesAsync(IEnumerable<GameFile> files,
        IEnumerable<LibraryItem> parents, VerifyMode mode, CancellationToken ct = default)
    {
        var fileList = files.Where(f => !string.IsNullOrEmpty(f.LocalRelativePath)).ToList();
        var result = await CheckFilesAsync(fileList, mode, ct);
        using (_manifest.Gate.Enter())   // (manifest gate 09-08)
            foreach (var item in parents)
                Sync.LibrarySyncService.RecomputeStatus(item);
        await _manifest.SaveAsync(ct);
        return result;
    }

    /// <summary>One gated field write. The per-file loop below awaits between checks (hashing, a server
    /// round-trip), so each state write takes the gate on its own rather than the loop holding it across an
    /// await. (manifest gate 09-08)</summary>
    private void Gated(Action write)
    {
        using (_manifest.Gate.Enter()) write();
    }

    /// <summary>Shared per-file check: presence + size, and MD5 per mode. Mutates States and tallies; callers
    /// roll up and persist for the scope they touched.</summary>
    private async Task<VerifyResult> CheckFilesAsync(List<GameFile> files, VerifyMode mode, CancellationToken ct)
    {
        var result = new VerifyResult();
        int done = 0;
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();

            // A file queued for re-fetch (Fix Now, manual retry) is NotBackedUp with its old
            // LocalRelativePath still recorded. That is not a health question: the manifest already says
            // we do not hold it, and the download rewrites these fields on completion. Re-flagging it from
            // the leftover path is how a fixed "gone from disk" error resurrected on every restart.
            if (f.State == FileState.NotBackedUp)
            {
                Progress?.Invoke(new VerifyProgress(++done, files.Count));
                continue;
            }

            string? path;
            if (Layout is not null)
            {
                // Root offline: not checkable, not an error -- count as skipped and move on.
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
                // No layout bound: resolve against the primary root, normalizing stored separators to native
                // (same rule as BackupLayout.ResolvePath).
                var native = f.LocalRelativePath!
                    .Replace('\\', Path.DirectorySeparatorChar)
                    .Replace('/', Path.DirectorySeparatorChar);
                path = Path.Combine(BackupRoot, native);
            }

            if (path is null || !File.Exists(path))
            {
                // A Delete that landed between our path resolve and this probe has already cleared the record
                // (LocalRelativePath null, NotBackedUp); flagging it Missing now would leave a Missing file with
                // no path. The user's delete is the newer fact (09-13).
                bool flagged = false;
                Gated(() => { if (!string.IsNullOrEmpty(f.LocalRelativePath)) { f.State = FileState.Missing; flagged = true; } });
                if (flagged) result.Missing++;
            }
            else
            {
                var size = new FileInfo(path).Length;
                // Size check prefers the exact LocalSizeBytes recorded at download time; ExpectedSizeBytes is a
                // rounded estimate from GOG's "63.8 MB" text and is only ever a loose sanity bound, never equality.
                bool sizeOk;
                if (f.LocalSizeBytes is { } exact)
                {
                    sizeOk = size == exact;
                }
                else if (f.ExpectedSizeBytes is { } est && est > 0)
                {
                    // No exact size on record (e.g. imported file): generous tolerance around the estimate.
                    var tolerance = Math.Max(1024L * 1024, (long)(est * 0.02)); // 2% or 1 MB
                    sizeOk = Math.Abs(size - est) <= tolerance;
                }
                else
                {
                    sizeOk = true; // nothing to compare against
                }
                if (!sizeOk)
                {
                    // Truncated / wrong size = corrupt. Do NOT overwrite LocalSizeBytes with the bad on-disk
                    // size: keeping the recorded good size is what keeps the mismatch detectable on later passes.
                    Gated(() => f.State = FileState.Corrupt);
                    result.Corrupt++;
                }
                // (09-25) No checksum on record: ask GOG for it (adopted files), when the caller opted in.
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
                // Read-frugal hashing: FullRehash re-hashes everything (bit-rot sweep); Unverified hashes only
                // checksummed files not already Verified; SizeOnly never hashes.
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
                        // A full content match is the only thing that clears Corrupt.
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
                        // Disk no longer matches our recorded hash. Ask GOG what it ships now: a different
                        // hash means a silent repack -- an update, not damage.
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
                            Gated(() => f.State = FileState.Corrupt);   // GOG still ships our hash (or we can't ask): real corruption
                            result.Corrupt++;
                        }
                    }
                }
                else if (f.State == FileState.Corrupt)
                {
                    // Size OK but already flagged Corrupt: a size-only pass can never heal that -- only a full
                    // MD5 match may. Leave it Corrupt.
                    result.Corrupt++;
                }
                else
                {
                    // Size OK, no hash (or fast mode): mark present-but-unverified. Never overwrite Verified
                    // (a stronger fact) or Outdated (sync-owned; a size check must not erase it).
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

/// <summary>How hard a verify pass works, trading disk reads for confidence.</summary>
public enum VerifyMode
{
    /// <summary>Existence + size only (a stat, no full read). Cheap; catches missing/truncated files.</summary>
    SizeOnly,
    /// <summary>Size, plus MD5 for checksummed files not already Verified. The default "Rescan &amp; Verify".</summary>
    Unverified,
    /// <summary>Size, plus MD5 for every checksummed file -- the deliberate bit-rot sweep ("Force full re-verify").</summary>
    FullRehash,
}

public sealed record VerifyProgress(int Done, int Total);

public sealed class VerifyResult
{
    public int Checked { get; set; }
    public int Verified { get; set; }
    public int SizeOnlyOk { get; set; }
    public int Corrupt { get; set; }
    /// <summary>Mismatches that GOG's current checksum proved are a changed build (silent repack), not damage.</summary>
    public int Updated { get; set; }
    public int Missing { get; set; }
    /// <summary>(09-25) Files with no checksum on record whose bytes differ from GOG's current checksum (older build
    /// or damaged). Record unchanged; counted in <see cref="SizeOnlyOk"/> too. Only a FetchMissingChecksums pass sets it.</summary>
    public int DiffersFromGog { get; set; }
    public int OfflineSkipped { get; set; }
}
