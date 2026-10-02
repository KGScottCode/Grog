// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grog.Core.Volumes;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grog.Core.Download;
using Grog.Core.Format;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Sync;
using Grog.Core.Verify;
using Grog.App.Runtime;

namespace Grog.App.ViewModels;

// Watch: the 2 s drive-presence poll (removable roots coming and going) and the CLI-presence check on the same tick.
public sealed partial class StorageViewModel
{
    // ---- Live drive-presence watch ----
    // A 2s poll re-probes drive presence and rebuilds device cards + grids ONLY when the online set changes.
    // Polling (not OS device-change hooks) keeps the same code working on Windows, macOS, and Linux.
    private System.Threading.Timer? _volumeWatch;
    private string _onlineSignature = "";
    private Dictionary<string, bool> _prevOnline = new();
    // Roots that just came back online: shown with a brief green "Reconnected" flash before settling.
    private readonly HashSet<string> _justReattached = new(StringComparer.OrdinalIgnoreCase);

    // ---- (09-25, owner) "Waiting for H: to respond…" ----
    // A drive that is slow to answer (asleep, busy with a big write, a USB hub) used to freeze or stall in silence.
    // Every real call into a volume's metadata is registered by DriveResolver while it runs; this half-second UI tick
    // reads that map (no I/O) and names a drive that has not answered for a second. Four places read it: the Overview's
    // Storage Status, the Storage page, the rail, and the Backup Task line while a run is live.
    private DispatcherTimer? _slowDriveTick;
    private readonly Dictionary<string, long> _slowSeen = new(StringComparer.OrdinalIgnoreCase);   // volume -> longest wait seen
    private string _waitingDriveText = "";
    /// <summary>"Waiting for H: to respond…", or "" when every drive answers promptly.</summary>
    public string WaitingDriveText => _waitingDriveText;
    public bool HasWaitingDrive => _waitingDriveText.Length > 0;
    /// <summary>The drive names alone ("H:" or "H: and G:"), for the lines that phrase it themselves.</summary>
    internal string WaitingDrives { get; private set; } = "";
    private IReadOnlyList<string> _waitingVolumes = System.Array.Empty<string>();
    private string WaitingTextFor(Grog.App.ViewModels.BackupLocationRow? row)
    {
        if (row is null || WaitingDrives.Length == 0 || string.IsNullOrEmpty(row.Path)) return "";
        var v = Grog.Core.Storage.DriveResolver.VolumeLabel(row.Path);   // a path computation, no I/O
        return _waitingVolumes.Contains(v, StringComparer.OrdinalIgnoreCase) ? $"Waiting for {v} to respond…" : "";
    }
    public string PrimaryWaitingText => WaitingTextFor(PrimaryLocation);
    public string SecondaryWaitingText => WaitingTextFor(SecondaryLocation);

    private void StartSlowDriveTick()
    {
        _slowDriveTick ??= new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => CheckSlowDrives());
        _slowDriveTick.Start();
    }

    internal void CheckSlowDrives()
    {
        {
            var slow = Grog.Core.Storage.DriveResolver.SlowVolumes(1000);
            foreach (var (v, ms) in slow) _slowSeen[v] = Math.Max(_slowSeen.TryGetValue(v, out var had) ? had : 0, ms);
            // A wait that ended: log it when it was long enough to matter.
            foreach (var v in _slowSeen.Keys.ToList())
                if (!slow.Any(x => string.Equals(x.Volume, v, StringComparison.OrdinalIgnoreCase)))
                {
                    if (_slowSeen[v] >= 10_000) _root.Log($"Drive {v} took {_slowSeen[v] / 1000.0:F0} s to respond.", category: LogCategory.General);
                    _slowSeen.Remove(v);
                }
            var names = string.Join(" and ", slow.Select(x => x.Volume));
            var text = names.Length == 0 ? "" : $"Waiting for {names} to respond…";
            if (text == _waitingDriveText) return;
            _waitingDriveText = text; WaitingDrives = names; _waitingVolumes = slow.Select(x => x.Volume).ToList();
            OnPropertyChanged(nameof(WaitingDriveText)); OnPropertyChanged(nameof(HasWaitingDrive));
            OnPropertyChanged(nameof(PrimaryWaitingText)); OnPropertyChanged(nameof(SecondaryWaitingText));
            OnPropertyChanged(nameof(RailStorageSub));
            _root.Overview.RaiseRunReadouts();   // the Backup Task line names it while a run waits
        }
    }

    /// <summary>True once the watch's first probe has landed; until then the cards paint no online/offline word
    /// (a UNC root's first probe can take the SMB timeout, and that wait never belongs on the dispatcher).</summary>
    private bool _watchSeeded;

    internal void StartVolumeWatch()
    {
        StartSlowDriveTick();
        // No probe here: the first tick seeds the online map off-thread and its ApplyVolumeChange paints the status.
        // One-shot, re-armed at the end of each tick: a slow probe never overlaps the next tick, and the whole body
        // is guarded because an exception in a thread-pool timer callback takes the process down.
        _volumeWatch ??= new System.Threading.Timer(_ => VolumeWatchTick(), null, TimeSpan.FromSeconds(2), System.Threading.Timeout.InfiniteTimeSpan);
    }

    private string? _volumeWatchLastError;
    private void VolumeWatchTick()
    {
        try
        {
            PollCliPresence();   // same 2 s worker tick: a blocked CLI verb, or a CLI writer running
            Dictionary<string, bool> now;
            try { now = OnlineMap(); } catch { return; }   // probe off the UI thread
            CheckWaitedDrivesBack(now);   // (09-25) every tick: a short flap does not change the signature
            var sig = Sig(now);
            if (sig == _onlineSignature && _watchSeeded) return;   // the first map always lands: it seeds the cards' status
            _onlineSignature = sig;
            // (09-15) Presence changed: re-measure every root's capacity sample HERE, on this worker, before the
            // cards rebuild. The rebuild reads StorageReport.DriveSample, a separate cache that otherwise still
            // holds the unplugged reading; with nothing else refreshing the cards while idle, a re-plugged drive
            // stayed "Offline".
            try
            {
                foreach (var r in _session.Manifest?.Current.Roots ?? Enumerable.Empty<BackupRoot>())
                    if (!string.IsNullOrEmpty(r.PathHint)) Grog.Core.Storage.StorageReport.RefreshSampleNow(r.PathHint);
            }
            catch { /* a probe that throws reads offline; the rebuild still runs */ }
            Dispatcher.UIThread.Post(() => ApplyVolumeChange(now));
        }
        catch (Exception ex)
        {
            // Logged once per distinct message: the tick keeps running, the log does not fill with repeats.
            if (ex.Message != _volumeWatchLastError)
            {
                _volumeWatchLastError = ex.Message;
                Dispatcher.UIThread.Post(() => _root.Log($"Storage watch: {ex.Message}", isError: true, category: LogCategory.General));
            }
        }
        finally
        {
            try { _volumeWatch?.Change(TimeSpan.FromSeconds(2), System.Threading.Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }
        }
    }

    // ---- The other host (09-08): the CLI shares this profile. The App has priority, so a CLI writer verb
    // that ran while we were open left a note (it was refused); a CLI verb that was already running when we
    // opened holds its own lock, and we warn once rather than fight it.
    private string? _cliRunningNoted;
    private void PollCliPresence()
    {
        string cfg;
        try { cfg = _session.ConfigDir.Length > 0 ? _session.ConfigDir : Grog.Core.Storage.GrogPaths.ResolveConfigDir(); } catch { return; }
        var blocked = Grog.Core.Storage.ProcessPresence.TakeBlockedNote(cfg);
        var running = Grog.Core.Storage.ProcessPresence.Holder(cfg, Grog.Core.Storage.ProcessPresence.CliLockName);
        if (blocked is null && running is null) { _cliRunningNoted = null; return; }
        Dispatcher.UIThread.Post(() =>
        {
            if (blocked is not null)
            {
                var msg = $"grogcli '{blocked}' was blocked: Grog is open and owns this profile. Close Grog to run it.";
                _root.Log(msg, category: LogCategory.General);
                _root.ShowToast(msg, 1);
            }
            if (running is { } r && _cliRunningNoted != r.Note)
            {
                _cliRunningNoted = r.Note;
                var msg = $"grogcli '{r.Note}' is running (pid {r.Pid}). Let it finish before backing up or changing storage here.";
                _root.Log(msg, category: LogCategory.General);
                _root.ShowToast(msg, 1);
            }
        });
    }

    private void ApplyVolumeChange(Dictionary<string, bool> now)
    {
        // A removable drive coming back online earns a short green "Reconnected" flash.
        bool presenceChanged = false;
        foreach (var (id, online) in now)
        {
            bool was = _prevOnline.TryGetValue(id, out var w) && w;
            var root = _session.Manifest?.Current.Roots.FirstOrDefault(r => r.Id == id);
            bool removable = root?.Removable ?? false;
            // (09-17) Presence changes are logged: the walk (and a user reading the log) could not tell WHEN a
            // drive went away or came back; the cards showed it and nothing recorded it.
            if (_prevOnline.ContainsKey(id) && was != online && root is not null)
            {
                _root.Log(online ? $"{DriveLabel(id)} is connected again." : $"{DriveLabel(id)} is not connected.", category: LogCategory.General);
                // The file rows read the root's State (ConditionRules.LookupFrom), which only a BackupLayout
                // construction wrote: the card said Disconnected while its rows still said Verified, and after a
                // re-plug the reverse (QA 09-18). Keep the slot states in step with the watch; a shelved drive
                // (Detached) is a place, not a presence, and is left alone.
                if (root.State is RootState.Online or RootState.Offline)
                {
                    _session.Manifest?.Mutate(_ => root.State = online ? RootState.Online : RootState.Offline);   // (manifest gate 09-08)
                    presenceChanged = true;
                    // (owner 09-25, Mac walk) Any storage leaving during a backup stops it, even when no file was
                    // writing there at that moment (the engine only notices when a file touches the drive).
                    if (!online) OnStorageDisconnected(id);
                    // Back, and still back after the same 4 s a waiting download gets (a drive spinning up flaps):
                    // a move that paused for this drive continues.
                    else if (_moveWaitsForRootId == id)
                        Avalonia.Threading.DispatcherTimer.RunOnce(() => { if (IsOnlineNow(id)) OnStorageBackForMove(id); }, TimeSpan.FromSeconds(4));
                }
            }
            if (!was && online && removable && _justReattached.Add(id))
            {
                Avalonia.Threading.DispatcherTimer.RunOnce(() =>
                {
                    if (_justReattached.Remove(id)) { RefreshBackupLocations(); PrimaryGrid.Raise(); SecondaryGrid.Raise(); RaiseInventory(); }
                }, TimeSpan.FromSeconds(2.2));
            }
        }
        _prevOnline = now;
        _watchSeeded = true;
        RefreshBackupLocations();
        if (presenceChanged) RebuildInventory();   // rows re-read the lookup built from the states just written
        PrimaryGrid.Raise(); SecondaryGrid.Raise(); RaiseInventory();
    }

    /// <summary>Which roots are mounted (live probe per root path); a change means rebuild the cards.</summary>
    private Dictionary<string, bool> OnlineMap()
    {
        var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (_session.Manifest?.Current is not { } m) return map;
        foreach (var r in m.Roots)
        {
            bool online = false;
            try
            {
                // DriveResolver finds the ACTUAL backing volume; a plain GetPathRoot probe reads "/" on Linux
                // and never goes offline.
                if (!string.IsNullOrEmpty(r.PathHint))
                    online = Grog.Core.Storage.DriveResolver.IsOnline(r.PathHint);
            }
            catch { /* unreadable path == offline */ }
            map[r.Id] = online;
        }
        return map;
    }

    private static string Sig(Dictionary<string, bool> map)
        => string.Join(";", map.OrderBy(k => k.Key).Select(k => k.Key + (k.Value ? '1' : '0')));
}
