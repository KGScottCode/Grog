// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grog.App.Runtime;
using Grog.Core.Models;

namespace Grog.App.ViewModels;

public sealed partial class ExtrasPlacementEditor : ObservableObject
{
    private readonly IExtrasPlacementHost _host;

    public ExtrasPlacementEditor(IExtrasPlacementHost host) => _host = host;

    private Grog.Core.Manifest.JsonManifestStore? Store => _host.Store;

    // ---- staged state -------------------------------------------------------------------------------
    // Staged is what the pills and the preview show; committed is what the manifest holds. The card is
    // dirty when they differ, and ONLY then does anything move.
    private ExtrasPlacement _stagedPlacement = ExtrasPlacement.WithGame;
    private string? _stagedExtrasRootId;
    private bool _loaded;

    public void ResetFromManifest()
    {
        var m = Store?.Current;
        _stagedPlacement = m?.ExtrasLayout ?? ExtrasPlacement.WithGame;
        _stagedExtrasRootId = CommittedExtrasRootId;
        _baselinePlacement = _stagedPlacement; _baselineExtrasRootId = _stagedExtrasRootId;
        _loaded = m is not null;
        _pendingMoves = null; _pendingBlockedBy = null;
        RaiseAll();
    }

    // What the card showed when it was last read from the manifest: the difference between "the USER staged
    // something" and "the committed state moved under an untouched card".
    private ExtrasPlacement _baselinePlacement = ExtrasPlacement.WithGame;
    private string? _baselineExtrasRootId;

    public void OnLocationsChanged()
    {
        if (!_loaded) return;
        // A scope change took extras out of scope: the card is hidden, and its staging went on living behind
        // it -- the leave guard asked about a card the user could no longer see, and "Apply" from that prompt
        // committed it. A hidden card holds nothing.
        if (!_host.ExtrasInScope) { if (IsDirty) ResetFromManifest(); return; }
        bool userStaged = _stagedPlacement != _baselinePlacement || _stagedExtrasRootId != _baselineExtrasRootId;
        bool stagedRootGone = _stagedExtrasRootId is { Length: > 0 } r && !_host.Locations.Any(l => l.RootId == r);
        if (!userStaged || stagedRootGone) { ResetFromManifest(); return; }
        RaiseAll();
        QueueCount();
    }

    private string? PrimaryRootId => Store?.Current.PrimaryRootId;

    private (string? RootId, string? Label) Secondary => _host.Locations.FirstOrDefault(l => l.RootId != PrimaryRootId);

    private string? SecondaryRootId => Secondary.RootId;

    public string? SecondaryName => string.IsNullOrWhiteSpace(Secondary.Label) ? null : Secondary.Label;

    private string? CommittedExtrasRootId
    {
        get
        {
            var m = Store?.Current;
            if (m is null) return null;
            return m.Routing.RoleRootOrPrimary(ContentRole.Extras, m.PrimaryRootId);
        }
    }

    // ---- what the pills show ------------------------------------------------------------------------
    public bool WithGameActive => _stagedPlacement == ExtrasPlacement.WithGame;
    public bool SeparateActive => !WithGameActive;
    public bool ByGameActive => _stagedPlacement == ExtrasPlacement.SeparateByGame;
    public bool ByTypeActive => _stagedPlacement == ExtrasPlacement.SeparateByType;
    public bool OnPrimaryActive => !OnSecondaryActive;
    public bool OnSecondaryActive => SeparateActive && _stagedExtrasRootId is { Length: > 0 } r && r != PrimaryRootId;

    public ExtrasPlacement StagedPlacement => _stagedPlacement;
    public bool StagedOnSecondary => OnSecondaryActive;

    // ---- which rows exist ---------------------------------------------------------------------------
    // Rows are ABSENT, not disabled, when they do not apply. A dimmed control that still looks clickable
    // is what produced the original bug report: it was clicked, and it did nothing visible.
    public bool HasSecondary => _host.Locations.Count >= 2;
    public bool ShowStorageRow => HasSecondary;

    public string SecondaryLabel => SecondaryName ?? "Secondary";

    // ---- dirty --------------------------------------------------------------------------------------
    public bool IsDirty
    {
        get
        {
            if (!_loaded || Store?.Current is not { } m) return false;
            if (_stagedPlacement != m.ExtrasLayout) return true;
            // WithGame ignores routing, so a stale role root is not a difference the user can see or act on.
            if (_stagedPlacement == ExtrasPlacement.WithGame) return false;
            return (_stagedExtrasRootId ?? PrimaryRootId) != (CommittedExtrasRootId ?? PrimaryRootId);
        }
    }

    public string PendingText => !IsDirty ? ""
        // A drive the move needs is not here: Apply will commit NOTHING, so the
        // footer must not promise a file count, or "nothing to move", that Apply cannot deliver.
        : _pendingBlockedBy is { Length: > 0 } drive ? $"Not applied · {drive} is not connected"
        : _pendingMoves is null ? "Not applied"
        : _pendingMoves == 0 ? "Not applied · nothing to move"
        : $"Not applied · {_pendingMoves} file{(_pendingMoves == 1 ? "" : "s")} will move";

    private int? _pendingMoves;
    private string? _pendingBlockedBy;

    // ---- choosing (stages only; nothing is written) --------------------------------------------------
    [RelayCommand]
    private void SelectShape(string? which)
    {
        var target = which == "withgame" ? ExtrasPlacement.WithGame
            // Coming back to a separate tree restores by-game, the default shape, rather than whatever was
            // last committed -- the user is choosing "separate", not re-choosing an ordering.
            : _stagedPlacement == ExtrasPlacement.SeparateByType ? ExtrasPlacement.SeparateByType
            : ExtrasPlacement.SeparateByGame;
        if (target == _stagedPlacement) return;
        _stagedPlacement = target;
        if (target == ExtrasPlacement.WithGame) _stagedExtrasRootId = PrimaryRootId;
        AfterStage();
    }

    [RelayCommand]
    private void SelectOrganize(string? which)
    {
        var target = which == "bytype" ? ExtrasPlacement.SeparateByType : ExtrasPlacement.SeparateByGame;
        if (target == _stagedPlacement) return;
        _stagedPlacement = target;
        AfterStage();
    }

    [RelayCommand]
    private void SelectStorage(string? which)
    {
        var target = which == "secondary" ? (SecondaryRootId ?? PrimaryRootId) : PrimaryRootId;
        if (target == _stagedExtrasRootId) return;
        _stagedExtrasRootId = target;
        AfterStage();
    }

    private void AfterStage()
    {
        RaiseAll();
        QueueCount();
    }

    // ---- the pending count, off the UI thread and debounced ------------------------------------------
    // Planning walks every file and builds a BackupLayout, which probes each root on disk. That is far too
    // much to run on a pill click, and a user picking a shape clicks two or three times in a second, so the
    // count is debounced and the stale answer is dropped rather than shown.
    private CancellationTokenSource? _countCts;
    private (ExtrasPlacement Placement, string? RootId)? _countedFor;

    private void QueueCount()
    {
        _countCts?.Cancel();
        // A re-count of the SAME staged choice keeps the last answer on screen until the new one lands. This
        // runs on every storage refresh (1 Hz during a backup), and blanking first made the footer flash between
        // "Not applied" and "Not applied - 279 files will move" once a second.
        var countingFor = (_stagedPlacement, _stagedExtrasRootId);
        if (!IsDirty || !Equals(countingFor, _countedFor))
        {
            _pendingMoves = null; _pendingBlockedBy = null;
            OnPropertyChanged(nameof(PendingText));
        }
        _countedFor = countingFor;
        if (!IsDirty) return;

        var cts = new CancellationTokenSource();
        _countCts = cts;
        var store = Store; var root = _host.BackupRoot;
        if (store is null || string.IsNullOrEmpty(root)) return;
        var placement = _stagedPlacement;
        var extrasRoot = _stagedExtrasRootId ?? PrimaryRootId;
        var primary = PrimaryRootId;
        if (string.IsNullOrEmpty(extrasRoot) || string.IsNullOrEmpty(primary)) return;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(250, cts.Token).ConfigureAwait(false);
                var m = store.Current;
                var layout = new Grog.Core.Volumes.BackupLayout(m, root);
                Grog.Core.Volumes.ReorgJob job;
                using (store.Gate.Enter())   // 
                    job = Grog.Core.Runs.ExtrasPlacementRun.PlanMoves(m, layout, placement, extrasRoot!, primary!);
                var n = job.Moves.Count;
                var blockedBy = job.HasUnavailableDrives ? string.Join(", ", job.UnavailableDrives) : null;
                if (cts.IsCancellationRequested) return;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!ReferenceEquals(_countCts, cts)) return;   // a later click already superseded this
                    _pendingMoves = n; _pendingBlockedBy = blockedBy;
                    OnPropertyChanged(nameof(PendingText));
                });
            }
            catch (OperationCanceledException) { }
            catch { /* a count is decoration; never fail a settings page over it */ }
        });
    }

    // ---- commit -------------------------------------------------------------------------------------
    [RelayCommand]
    private void Discard() => ResetFromManifest();

    [RelayCommand]
    private async Task Apply()
    {
        if (!IsDirty) return;
        var store = Store; var root = _host.BackupRoot;
        if (store is null || string.IsNullOrEmpty(root)) return;
        var placement = _stagedPlacement;
        var extrasRoot = _stagedExtrasRootId ?? PrimaryRootId;
        if (string.IsNullOrEmpty(extrasRoot)) return;

        try
        {
            var plan = await Grog.Core.Runs.ExtrasPlacementRun.ApplyAndPlanAsync(
                store, root, placement, extrasRoot!, _host.SessionToken);

            // A drive the move needs is not here: NOTHING was committed, so the card stays staged and
            // Apply is still there when the drive is back. The host names the drive.
            if (plan is { HasUnavailableDrives: true }) { _host.OfferPlan(plan); return; }

            // Committed: a preference with no files to move is not a failure, and the user must never have to
            // press Apply twice to make a choice stick.
            _host.AfterCommit();
            ResetFromManifest();

            if (plan is null) return;
            _host.OfferPlan(plan);
        }
        catch (Exception ex)
        {
            _host.Log($"Couldn't apply the extras placement: {ex.Message}", isError: true);
        }
    }

    // ---- leave guard ---------------------------------------------------------------------------------
    public bool HasPending => IsDirty && _host.ExtrasInScope;

    [ObservableProperty] private bool _showLeavePrompt;
    private string? _pendingNav;

    internal void AskBeforeLeaving(string? target)
    {
        _pendingNav = target;
        ShowLeavePrompt = true;
    }

    [RelayCommand]
    private void StayHere() { ShowLeavePrompt = false; _pendingNav = null; }

    [RelayCommand]
    private void DiscardAndLeave()
    {
        ShowLeavePrompt = false;
        var target = _pendingNav; _pendingNav = null;
        ResetFromManifest();
        _host.LeaveTo(target);
    }

    [RelayCommand]
    private async Task ApplyAndLeave()
    {
        ShowLeavePrompt = false;
        var target = _pendingNav; _pendingNav = null;
        await Apply();
        // Still staged: Apply was blocked (a drive the move needs is offline) and nothing was committed. Stay,
        // or the page change would discard the very choice the user just asked to keep.
        if (IsDirty) { _host.Log("Not applied: reconnect the drive named on the Storage page, then Apply again."); return; }
        // Apply may have raised the move prompt, which lives on the Storage page. Leaving now would hide a
        // question the user has not answered, so say where it went rather than let it go unnoticed.
        if (_host.MigratePromptOpen)
        {
            _host.Log("Saved. Grog has files to move for the new extras layout; the Storage page is asking.");
            // The destination was the window's X: closing now would drop the question unanswered, with the new
            // layout committed and the files still in the old one. Go to the question instead;
            // the close is the user's to press again once it is answered.
            if (target == MainWindowViewModel.CloseTarget) { _host.LeaveTo(StoragePage); return; }
        }
        _host.LeaveTo(target);
    }

    internal const string StoragePage = "Drives";

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(WithGameActive)); OnPropertyChanged(nameof(SeparateActive));
        OnPropertyChanged(nameof(ByGameActive)); OnPropertyChanged(nameof(ByTypeActive));
        OnPropertyChanged(nameof(OnPrimaryActive)); OnPropertyChanged(nameof(OnSecondaryActive));
        OnPropertyChanged(nameof(StagedPlacement)); OnPropertyChanged(nameof(StagedOnSecondary));
        OnPropertyChanged(nameof(HasSecondary)); OnPropertyChanged(nameof(ShowStorageRow));
        OnPropertyChanged(nameof(SecondaryLabel));
        OnPropertyChanged(nameof(IsDirty)); OnPropertyChanged(nameof(HasPending));
        OnPropertyChanged(nameof(PendingText));
        // The preview lives on the Settings VM and reads this editor's STAGED values, so it re-raises on
        // every stage. Deliberately NOT AfterCommit: that re-reads the locations off disk, and a pill click
        // must not do disk work.
        _host.RaiseFolderPreview();
    }
}
