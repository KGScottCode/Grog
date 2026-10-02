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

/// <summary>
/// The "Where extras are stored" card: three choices STAGED together and committed once.
///
/// Why this is the page's only staged card (owner call 09-11). The three pills are not peers -- the shape
/// decides whether the other two mean anything -- and each used to commit on click through its own path,
/// so "separate folder, on the second drive" was two commits, two prompts, and one of them silently did
/// nothing (see <see cref="Grog.Core.Runs.ExtrasPlacementRun"/>). Staging makes the hierarchy real: nothing
/// happens until the whole shape is chosen, and one Apply produces one move job.
///
/// It lives in its own class, not on SettingsViewModel, for the reason the S3 split exists: the settings
/// view model is a composition root, and a card with staged state, dirty tracking, a debounced background
/// planner and a leave guard is a component, not a property bag.
/// </summary>
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

    /// <summary>Re-reads the committed state and drops any staging. Called when the store opens, when the
    /// backup locations change under us, and by Discard.</summary>
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

    /// <summary>The storage locations changed (a drive added, removed, detached, swapped). The card read them
    /// ONCE, when the store opened, so removing the secondary left it staged on a drive that no longer
    /// exists: dirty with nothing the user did, the leave guard asking about it, and Apply ready to route
    /// extras to a missing root (sweep 2 #19). An untouched card follows the manifest; a staged choice that
    /// names a drive that is gone is dropped; a staged choice that still makes sense is kept and re-counted.</summary>
    public void OnLocationsChanged()
    {
        if (!_loaded) return;
        // A scope change took extras out of scope: the card is hidden, and its staging went on living behind
        // it -- the leave guard asked about a card the user could no longer see, and "Apply" from that prompt
        // committed it (sweep 2 #29). A hidden card holds nothing.
        if (!_host.ExtrasInScope) { if (IsDirty) ResetFromManifest(); return; }
        bool userStaged = _stagedPlacement != _baselinePlacement || _stagedExtrasRootId != _baselineExtrasRootId;
        bool stagedRootGone = _stagedExtrasRootId is { Length: > 0 } r && !_host.Locations.Any(l => l.RootId == r);
        if (!userStaged || stagedRootGone) { ResetFromManifest(); return; }
        RaiseAll();
        QueueCount();
    }

    private string? PrimaryRootId => Store?.Current.PrimaryRootId;

    /// <summary>THE secondary lookup: the first location that is not the primary (default tuple when none).</summary>
    private (string? RootId, string? Label) Secondary => _host.Locations.FirstOrDefault(l => l.RootId != PrimaryRootId);

    private string? SecondaryRootId => Secondary.RootId;

    /// <summary>The secondary's own name, or null when it has none (or there is no secondary).</summary>
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

    /// <summary>Staged placement, for the folder preview. The preview must show what APPLY WILL DO, so it
    /// reads staging, never the manifest.</summary>
    public ExtrasPlacement StagedPlacement => _stagedPlacement;
    /// <summary>Staged "extras live on the other device", for the preview's secondary block.</summary>
    public bool StagedOnSecondary => OnSecondaryActive;

    // ---- which rows exist ---------------------------------------------------------------------------
    // Rows are ABSENT, not disabled, when they do not apply. A dimmed control that still looks clickable
    // is what produced the original bug report: it was clicked, and it did nothing visible.
    /// <summary>There is a second place to put things.</summary>
    public bool HasSecondary => _host.Locations.Count >= 2;
    /// <summary>"Which storage holds them" -- purely "is there a second drive". Whether the sub-rows apply
    /// AT ALL is the shape's business and the view handles it by reserving their space rather than
    /// collapsing it, so this must NOT also test the shape: doing both made the card change height when the
    /// shape toggled (measured 09-11).</summary>
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

    /// <summary>Footer line. The file count is what makes Apply a considered click rather than a shrug, so
    /// it is stated as soon as the background plan answers; until then the line is honest about not knowing.</summary>
    public string PendingText => !IsDirty ? ""
        // A drive the move needs is not here: Apply will commit NOTHING (plan before commit, 09-13), so the
        // footer must not promise a file count, or "nothing to move", that Apply cannot deliver (sweep 2 #30).
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
        // (09-20) A re-count of the SAME staged choice keeps the last answer on screen until the new one lands. This
        // runs on every storage refresh (1 Hz during a backup), and blanking first made the footer flash between
        // "Not applied" and "Not applied - 279 files will move" once a second (owner-hit, walk pass 4).
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
                using (store.Gate.Enter())   // (manifest gate 09-08)
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

            // A drive the move needs is not here: NOTHING was committed (09-13), so the card stays staged and
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
    /// <summary>The shell asks before navigating away; staged-but-unapplied is the one state on this page
    /// that a page change would silently discard.</summary>
    public bool HasPending => IsDirty && _host.ExtrasInScope;

    /// <summary>Shown over the Settings page while the leave question stands. An overlay, not a modal
    /// dialog, to match the migrate prompt this card's Apply feeds into.</summary>
    [ObservableProperty] private bool _showLeavePrompt;
    private string? _pendingNav;

    /// <summary>The shell caught a page change with staged choices. Hold the destination and ask.</summary>
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
            // layout committed and the files still in the old one (sweep 2 #22). Go to the question instead;
            // the close is the user's to press again once it is answered.
            if (target == MainWindowViewModel.CloseTarget) { _host.LeaveTo(StoragePage); return; }
        }
        _host.LeaveTo(target);
    }

    /// <summary>The rail key of the Storage page, where the move prompt lives.</summary>
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
