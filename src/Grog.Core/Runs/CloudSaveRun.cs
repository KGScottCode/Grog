// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Grog.Core.Auth;
using Grog.Core.CloudSaves;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Volumes;

namespace Grog.Core.Runs;

public enum CloudSaveSelectionKind { All = 0, Games = 1, Account = 2, Entry = 3 }

public sealed record CloudSaveSelection(CloudSaveSelectionKind Kind, IReadOnlyList<long> GameIds, string? AccountId)
{
    public static CloudSaveSelection All { get; } = new(CloudSaveSelectionKind.All, Array.Empty<long>(), null);
    public static CloudSaveSelection ForGames(IEnumerable<long> gogIds) => new(CloudSaveSelectionKind.Games, gogIds.Distinct().ToList(), null);
    public static CloudSaveSelection ForAccount(string accountId) => new(CloudSaveSelectionKind.Account, Array.Empty<long>(), accountId);
    public static CloudSaveSelection ForEntry(long gogId, string accountId) => new(CloudSaveSelectionKind.Entry, new[] { gogId }, accountId);

    internal bool Includes(LibraryItem item, CloudAccountSave entry) => Kind switch
    {
        CloudSaveSelectionKind.Games => GameIds.Contains(item.GogId),
        CloudSaveSelectionKind.Account => string.Equals(entry.AccountId, AccountId ?? "", StringComparison.Ordinal),
        CloudSaveSelectionKind.Entry => GameIds.Contains(item.GogId) && string.Equals(entry.AccountId, AccountId ?? "", StringComparison.Ordinal),
        _ => true,
    };
}

public sealed record CloudSaveEntryResult(
    long GogId, string Title, string AccountId,
    int FilesWritten, int FilesExpected, string? LocalSaveDir, int Pruned, bool Skipped, string? Error)
{
    public bool Ok => !Skipped && Error is null && FilesWritten > 0;
    public bool Failed => !Skipped && !Ok;
    public bool Partial => Ok && FilesWritten < FilesExpected;
}

public sealed record CloudSaveRunResult(IReadOnlyList<CloudSaveEntryResult> Entries, bool Canceled)
{
    public int Ok => Entries.Count(e => e.Ok);
    public int Failed => Entries.Count(e => e.Failed);
    public int Skipped => Entries.Count(e => e.Skipped);
    public int Pruned => Entries.Sum(e => e.Pruned);
}

public sealed record CloudSavePruneEntry(long GogId, string Title, string AccountId, int Deleted, int Remaining, string GameDir);

public sealed record CloudSavePruneResult(IReadOnlyList<CloudSavePruneEntry> Entries, int KeepPerGame)
{
    public int Deleted => Entries.Sum(e => e.Deleted);
    public int Touched => Entries.Count(e => e.Deleted > 0);
}

public sealed class CloudSaveRun
{
    private readonly IManifestStore _store;
    private readonly HttpClient _http;
    private readonly IAuthService _legacyAuth;
    private readonly AccountSessions? _sessions;
    private readonly string _backupRoot;
    private readonly IBackupHost _host;

    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.Now;

    public Func<string, CloudSaveService> ServiceFor { get; init; }

    public CloudSaveRun(IManifestStore store, HttpClient http, IAuthService legacyAuth, AccountSessions? sessions,
                        string backupRoot, IBackupHost host)
    {
        _store = store; _http = http; _legacyAuth = legacyAuth; _sessions = sessions; _backupRoot = backupRoot; _host = host;
        ServiceFor = accountId =>
            _sessions is { } s && s.Accounts.Count > 0
                ? new CloudSaveService(s.Http, s.AuthFor(accountId))
                : new CloudSaveService(_http, _legacyAuth);
    }

    public static bool IsUpToDate(CloudAccountSave e)
    {
        if (e.LocalSaveCount <= 0) return false;
        if (e.BackedUpFiles < e.Files) return false;
        if (e.UpdatedUtc is { } now && e.BackedUpChangeUtc is { } then) return now <= then;
        return e.BackedUpSize == e.SizeBytes && e.BackedUpFiles == e.Files;
    }

    public IReadOnlyList<(LibraryItem Item, CloudAccountSave Entry)> Targets(CloudSaveSelection selection)
    {
        using (_store.Gate.Enter())   // 
        {
            var m = _store.Current;
            CloudSaveReconciler.SeedLegacy(m);
            var list = new List<(LibraryItem, CloudAccountSave)>();
            foreach (var it in m.Items.Where(i => i.HasCloudSaves ?? false).OrderBy(i => i.Title, StringComparer.OrdinalIgnoreCase))
                foreach (var e in it.CloudByAccount)
                    if (selection.Includes(it, e)) list.Add((it, e));
            return list;
        }
    }

    public async Task<CloudSaveRunResult> DownloadAsync(CloudSaveSelection selection, int keepPerGame, CancellationToken ct,
                                                        bool skipUpToDate = true)
    {
        var results = new List<CloudSaveEntryResult>();
        var targets = Targets(selection);
        if (selection.Kind == CloudSaveSelectionKind.Games)
            foreach (var id in selection.GameIds.Where(id => targets.All(t => t.Item.GogId != id)))
            {
                var title = _store.Read(m => m.ItemById(id)?.Title) ?? id.ToString();
                results.Add(new CloudSaveEntryResult(id, title, "", 0, 0, null, 0, false,
                    "No cloud saves are known for this game. Run 'cloudsaves list' (or Check for new saves) first."));
            }
        if (targets.Count == 0) return new CloudSaveRunResult(results, false);

        // One layout per run
        var layout = await Task.Run(() => new BackupLayout(_store.Current, _backupRoot), ct);
        bool canceled = false;
        int n = 0;
        foreach (var (item, entry) in targets)
        {
            if (ct.IsCancellationRequested) { canceled = true; break; }
            n++;
            if (skipUpToDate && IsUpToDate(entry))
            {
                results.Add(new CloudSaveEntryResult(item.GogId, item.Title, entry.AccountId, 0, 0, null, 0, true, null));
                continue;
            }
            _host.Log($"Backing up cloud saves… {n}/{targets.Count} - {item.Title}");
            try
            {
                results.Add(await DownloadOneAsync(item, entry, layout, keepPerGame, ct));
            }
            catch (OperationCanceledException) { canceled = true; break; }
            catch (Exception ex)
            {
                results.Add(new CloudSaveEntryResult(item.GogId, item.Title, entry.AccountId, 0, 0, null, 0, false, ex.Message));
            }
            var last = results[^1];
            if (last.Ok) _host.Log($"{AccountPrefix(entry.AccountId)}cloud saves backed up: {item.Title} ({Format.Plural.Of(last.FilesWritten, "file")})");
            else _host.Log($"{AccountPrefix(entry.AccountId)}cloud saves FAILED: {item.Title}" + (last.Error is { } err ? $" ({err})" : " (GOG refused, 0 files)"), isError: true);
        }
        return new CloudSaveRunResult(results, canceled || ct.IsCancellationRequested);
    }

    private async Task<CloudSaveEntryResult> DownloadOneAsync(LibraryItem item, CloudAccountSave entry, BackupLayout layout,
                                                              int keepPerGame, CancellationToken ct)
    {
        var accountId = entry.AccountId;
        var cloud = ServiceFor(accountId);   // the saves belong to THIS entry's account
        cloud.Log ??= m => _host.Log(m, isError: true);   // skipped entries reach the host's log
        var clientId = item.CloudClientId;
        if (string.IsNullOrEmpty(clientId))   // resolve once (only the games that actually have saves)
        {
            clientId = await cloud.ResolveClientIdAsync(item.GogId, ct: ct) ?? "";
            if (!string.IsNullOrEmpty(clientId))
            {
                var cid = clientId;
                _store.Mutate(_ => item.CloudClientId = cid);   // 
                await _store.SaveAsync(ct);
            }
        }
        if (string.IsNullOrEmpty(clientId))
            return new CloudSaveEntryResult(item.GogId, item.Title, accountId, 0, 0, null, 0, false,
                "Couldn't resolve the game's cloud client id (no build metadata).");

        var slug = !string.IsNullOrEmpty(item.Slug) ? item.Slug : item.Title;
        var gameDir = GameDir(layout, accountId, slug);
        var (dir, written, expected) = await cloud.DownloadLocalSaveAsync(new CloudGame(item.GogId, item.Title, slug, clientId), gameDir, ct);
        if (written <= 0)
            return new CloudSaveEntryResult(item.GogId, item.Title, accountId, 0, expected, null, 0, false, null);

        int pruned = 0; int? countOnDisk = null;
        try
        {
            if (written >= expected) pruned = LocalSaveArchive.PruneToKeep(gameDir, keepPerGame);
            countOnDisk = LocalSaveArchive.List(gameDir).Count;
        }
        catch { /* best-effort prune; the local save itself is already safely written */ }

        var now = Now();
        using (_store.Gate.Enter())   // 
        {
            entry.LastBackup = now;
            entry.LocalSaveCount += 1;
            entry.BackedUpFiles = written;
            if (written >= expected)
            {
                entry.BackedUpSize = entry.SizeBytes;
                entry.BackedUpChangeUtc = entry.UpdatedUtc;
            }
            if (countOnDisk is int c) entry.LocalSaveCount = c;
            CloudSaveReconciler.MirrorLegacy(item);   // legacy fields follow the first entry
        }
        await _store.SaveAsync(ct);
        return new CloudSaveEntryResult(item.GogId, item.Title, accountId, written, expected, dir, pruned, false, null);
    }

    public async Task<CloudSavePruneResult> PruneAsync(int keepPerGame, CancellationToken ct)
    {
        var entries = new List<CloudSavePruneEntry>();
        if (keepPerGame <= 0) return new CloudSavePruneResult(entries, keepPerGame);
        var targets = Targets(CloudSaveSelection.All);
        if (targets.Count == 0) return new CloudSavePruneResult(entries, keepPerGame);
        var layout = await Task.Run(() => new BackupLayout(_store.Current, _backupRoot), ct);
        bool changed = false;
        foreach (var (item, entry) in targets)
        {
            ct.ThrowIfCancellationRequested();
            var slug = !string.IsNullOrEmpty(item.Slug) ? item.Slug : item.Title;
            var gameDir = GameDir(layout, entry.AccountId, slug);
            int deleted; IReadOnlyList<LocalSaveArchive.LocalSave> remaining;
            try
            {
                deleted = await Task.Run(() => LocalSaveArchive.PruneToKeep(gameDir, keepPerGame), ct);
                remaining = await Task.Run(() => LocalSaveArchive.List(gameDir), ct);
            }
            catch (Exception ex)
            {
                _host.Log($"Couldn't prune {item.Title}: {ex.Message}", isError: true);
                continue;
            }
            if (deleted > 0)
            {
                using (_store.Gate.Enter())   // 
                    CloudSaveReconciler.RecordLocalSaves(item, entry.AccountId, remaining.Count,
                        remaining.Count > 0 ? remaining[0].StampUtc : (DateTimeOffset?)null);
                changed = true;
                _host.Log($"{AccountPrefix(entry.AccountId)}{item.Title}: removed {Format.Plural.Of(deleted, "older local save")}, {remaining.Count} kept.");
            }
            entries.Add(new CloudSavePruneEntry(item.GogId, item.Title, entry.AccountId, deleted, remaining.Count, gameDir));
        }
        if (changed) await _store.SaveAsync(ct);
        return new CloudSavePruneResult(entries, keepPerGame);
    }

    private string GameDir(BackupLayout layout, string accountId, string slug)
    {
        var cloudBase = layout.ResolveCloudBaseDir(out _) ?? Path.Combine(_backupRoot, "Cloud Saves");
        return CloudSaveReconciler.GameArchiveDir(_store.Current, cloudBase, accountId, slug);
    }

    private string AccountPrefix(string accountId)
    {
        var m = _store.Current;
        if (m.Accounts.Count <= 1) return "";
        var a = m.Accounts.FirstOrDefault(x => x.Id == accountId);
        var name = a is null ? (accountId.Length == 0 ? "primary" : accountId)
                 : string.IsNullOrEmpty(a.Username) ? (a.Id.Length == 0 ? "primary" : a.Id) : a.Username;
        return $"account: {name} - ";
    }
}
