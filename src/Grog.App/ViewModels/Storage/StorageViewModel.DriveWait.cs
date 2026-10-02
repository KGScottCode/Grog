// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Grog.Core.Manifest;
using Grog.Core.Volumes;

namespace Grog.App.ViewModels;

/// <summary>
/// (09-25, owner) Storage that went away mid-download: EVERYTHING stops. A missing drive is a broken state the user
/// addresses before Grog continues; planning around a drive that may never come back is more than the app should guess.
///   - The run pauses (the app's one Pause). Files headed to the drive take no strike, are not moved, and are not
///     marked "no room" (Core: DriveWaits, DownloadSettlement.DriveOffline).
///   - A dialog says so: [Go to Storage] [OK].
///   - Reconnect it (same folder, back for 4 s) and downloads continue on their own, unless the user paused too.
///   - Or Mark as Lost on the Storage page: the drive leaves Grog, the wait is dropped, its files re-queue elsewhere.
/// Every queue read and manifest write here runs off the UI thread.
/// </summary>
public sealed partial class StorageViewModel
{
    private string? _driveGoneRootId;        // the dialog's drive; null = no dialog
    // The drives Grog paused for. Reconnecting continues only THAT pause, once every one of them is back (review 09-25).
    private readonly HashSet<string> _pausedForDrives = new(StringComparer.Ordinal);
    internal void ForgetDrivePause() => _pausedForDrives.Clear();
    /// <summary>Stop emptied the queue: nothing waits for a drive any more, so the pause and its dialog go too.</summary>
    internal void ForgetDriveWait()
    {
        _pausedForDrives.Clear();
        _driveGoneRootId = null;
        RaiseDriveWait();
    }
    private readonly HashSet<string> _backPending = new(StringComparer.Ordinal);

    private string DriveLabel(string? id)
    {
        var m = _session.Manifest?.Current;
        var r = m?.Roots.FirstOrDefault(x => x.Id == id);
        if (r is null) return "Storage";
        // "Small" (Secondary): the name the owner gave it, quoted, then the slot it fills (walk 5.6).
        var slot = SlotNameOf(id);
        if (string.IsNullOrWhiteSpace(r.Label)) return slot.Length > 0 ? slot : "Storage";
        return slot.Length > 0 && slot != r.Label ? $"\"{r.Label}\" ({slot})" : $"\"{r.Label}\"";
    }

    /// <summary>The drive Grog paused for, while that pause stands: the Overview's "why paused" line names it.</summary>
    internal string? PausedForDriveLabel
        => _root.DownloadPaused && _pausedForDrives.FirstOrDefault(DriveWaits.Contains) is { } id ? DriveLabel(id) : null;

    public bool ShowDriveWaitCard => _driveGoneRootId is not null;
    public string DriveWaitTitle => $"{DriveLabel(_driveGoneRootId)} is disconnected. Downloads are paused.";
    public string DriveWaitBody
        => $"Reconnect {DriveLabel(_driveGoneRootId)} to continue, or mark it as lost on the Storage page to download its files somewhere else.";

    internal void RaiseDriveWait()
    {
        foreach (var n in new[] { nameof(ShowDriveWaitCard), nameof(DriveWaitTitle), nameof(DriveWaitBody) })
            OnPropertyChanged(n);
    }

    /// <summary>The presence watch saw a storage go away. During a backup that stops everything, like the engine's
    /// own detection; while idle the Storage card's "Disconnected" already says it.</summary>
    internal void OnStorageDisconnected(string rootId)
    {
        if (_session.LiveEngine is null || _session.StopRequested || !_root.DownloadRunning) return;
        // The user's own pause: idle, and the Storage card says it. Grog's drive pause: a second drive leaving waits too.
        if (_root.DownloadPaused && _pausedForDrives.Count == 0) return;
        DriveWaits.Add(rootId);   // its files wait: not re-planned elsewhere, not "no room"
        OnDriveWentAway(rootId);
    }

    /// <summary>The engine found a file's storage gone, or the watch saw it leave mid-backup: stop everything.</summary>
    internal void OnDriveWentAway(string rootId)
    {
        if (string.IsNullOrEmpty(rootId) || _pausedForDrives.Contains(rootId)) return;   // both detectors fire: act once
        _root.Log($"{DriveLabel(rootId)} was disconnected during the backup. Downloads are paused until it is back.",
                  isError: true, category: LogCategory.Download);
        if (!_root.DownloadPaused)
        {
            _root.PauseDownloads();
            _pausedForDrives.Add(rootId);   // after the pause: its SetDownloadPaused must not clear this
        }
        else if (_pausedForDrives.Count > 0) _pausedForDrives.Add(rootId);   // a second drive during Grog's own pause
        _driveGoneRootId = rootId;
        RaiseDriveWait();
    }

    /// <summary>Kept for the run-end hook: the dialog needs no recount now.</summary>
    internal void RefreshDriveWait() => RaiseDriveWait();

    private bool IsOnlineNow(string id) => _prevOnline.TryGetValue(id, out var on) && on;

    /// <summary>Something waits on this drive: the registry, or the live engine's own offline mark.</summary>
    private bool IsWaitedOn(string id)
        => DriveWaits.Contains(id) || (_session.LiveEngine?.OfflineRoots.Contains(id) ?? false);

    /// <summary>Every watch tick, not only on a presence change: a flap shorter than the 2 s poll leaves the online
    /// signature unchanged (review 09-25). Called on the watch's worker thread with its fresh probe.</summary>
    private void CheckWaitedDrivesBack(Dictionary<string, bool> now)
    {
        var engineOff = _session.LiveEngine?.OfflineRoots ?? (IReadOnlyCollection<string>)Array.Empty<string>();
        List<Grog.Core.Models.BackupRoot> roots;
        try { roots = _session.Manifest?.Current.Roots.ToList() ?? new List<Grog.Core.Models.BackupRoot>(); }
        catch (InvalidOperationException) { return; }   // a concurrent edit: the next tick reads a settled list
        bool dropped = false;
        foreach (var id in DriveWaits.Roots.Concat(engineOff).Distinct())
        {
            // Marked as Lost (removed from Grog) or put on the shelf (Detached): it is not coming back as itself.
            // Stop waiting so its files are planned like any others.
            var root = roots.FirstOrDefault(r => r.Id == id);
            if (root is null || root.State == Grog.Core.Models.RootState.Detached)
            {
                dropped |= DriveWaits.Remove(id);
                continue;
            }
            if (now.TryGetValue(id, out var on) && on) Dispatcher.UIThread.Post(() => NoteWaitingDriveBack(id));
        }
        if (dropped) Dispatcher.UIThread.Post(() =>
        {
            if (_driveGoneRootId is { } g && !DriveWaits.Contains(g)) _driveGoneRootId = null;
            _pausedForDrives.RemoveWhere(id => !DriveWaits.Contains(id));   // marked lost: no longer waited for
            RaiseDriveWait();
            _ = _root.Overview.ReplanQueueAsync("a disconnected drive was removed");
        });
    }

    /// <summary>The watch saw a waiting drive's folder again. Act only once it has stayed for 4 s (a flaky
    /// cable or a drive still spinning up flaps), and only if its folder is really there.</summary>
    private void NoteWaitingDriveBack(string id)
    {
        if (!_backPending.Add(id)) return;
        DispatcherTimer.RunOnce(async () =>
        {
            try
            {
                if (!IsOnlineNow(id) || !IsWaitedOn(id)) return;
                var path = _session.Manifest?.Current.Roots.FirstOrDefault(r => r.Id == id)?.PathHint;
                bool there = !string.IsNullOrEmpty(path) && await Task.Run(() => System.IO.Directory.Exists(path));
                if (!there || !IsWaitedOn(id)) return;
                await DriveBackAsync(id);
            }
            finally { _backPending.Remove(id); }
        }, TimeSpan.FromSeconds(4));
    }

    /// <summary>The drive is back. If Grog paused for it (and the user has not paused or resumed since), downloads
    /// continue; otherwise a toast says it is back and the user's own Pause stands.</summary>
    private async Task DriveBackAsync(string id)
    {
        var engine = _session.LiveEngine;
        await Task.Run(() => { if (engine is not null) engine.RootBack(id); });   // the engine's retire lock: off the UI thread
        DriveWaits.Remove(id);
        if (_driveGoneRootId == id) _driveGoneRootId = null;
        RaiseDriveWait();
        var name = DriveLabel(id);
        await _root.Overview.ReplanQueueAsync($"{name} is back");
        bool ours = _pausedForDrives.Remove(id) && _root.DownloadPaused;
        if (ours && _pausedForDrives.Count == 0 && !DriveWaits.Roots.Any() && _root.ResumeDownloadsCommand.CanExecute(null))
        {
            _root.Log($"{name} is back. Downloads continue.", category: LogCategory.Download);
            _root.ShowToast($"{name} is back. Downloads continue.", 0);
            _root.ResumeDownloadsCommand.Execute(null);
            return;
        }
        _root.Log($"{name} is back.", category: LogCategory.Download);
        _root.ShowToast(_root.DownloadPaused ? $"{name} is back. Resume when you are ready." : $"{name} is back.", 0);
    }

    /// <summary>OK: close the dialog. Downloads stay paused until the drive is back or Resume is pressed.</summary>
    [RelayCommand]
    private void DriveWaitKeep()
    {
        _driveGoneRootId = null;
        RaiseDriveWait();
    }

    /// <summary>Go to Storage: where Mark as Lost lives.</summary>
    [RelayCommand]
    private void DriveWaitGoToStorage()
    {
        _driveGoneRootId = null;
        RaiseDriveWait();
        _root.NavigateCommand.Execute("Drives");
    }
}
