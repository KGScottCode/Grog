// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Linq;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Grog.App.ViewModels;

/// <summary>
/// The first-run guide: spotlight geometry (measured by the view, never computed here), caption placement,
/// step text, and the cursor over the tour (Grog.Core.Guide.GuideSession). Reads the window through
/// <see cref="IGuideHost"/> and tells it about changes through <see cref="Changed"/>; nothing here names a
/// host property. Extracted from MainWindowViewModel.Guide.cs 2026-09-02 with the bound names unchanged, so
/// the overlay in MainWindow.axaml rebinds by DataContext alone.
/// </summary>
public sealed partial class GuideViewModel : ObservableObject
{
    private readonly IGuideHost _host;
    internal GuideViewModel(IGuideHost host) => _host = host;

    // Spotlight geometry is measured, never computed: the view TranslatePoints the target control into the
    // overlay's coordinate space and writes the answer here, so DPI, window size and a collapsed rail follow.
    [ObservableProperty] private bool _guideActive;
    [ObservableProperty] private double _guideHoleX, _guideHoleY, _guideHoleW, _guideHoleH;
    /// <summary>False when the current step's control is not on screen; the scrim drops entirely then,
    /// because a spotlight dimming the app with no target is indistinguishable from a freeze.</summary>
    [ObservableProperty] private bool _guideHasTarget;

    // Margins derived from the hole: scrim rects and caption are relative to the measured rectangle.
    public Thickness GuideHoleTopMargin    => new(GuideHoleX, GuideHoleY, 0, 0);
    /// <summary>The LEFT scrim band starts at x=0 and is GuideHoleX wide; it must not also be offset by X.</summary>
    public Thickness GuideHoleLeftMargin   => new(0, GuideHoleY, 0, 0);
    public Thickness GuideHoleRightMargin  => new(GuideHoleX + GuideHoleW, GuideHoleY, 0, 0);
    public Thickness GuideHoleBottomMargin => new(0, GuideHoleY + GuideHoleH, 0, 0);
    /// <summary>Viewport size, written by the view from the overlay's own bounds; the caption needs it to
    /// keep the bubble from being placed past the window edge.</summary>
    [ObservableProperty] private double _guideViewportW, _guideViewportH;

    private const double CaptionGap = 18, CaptionEdge = 12;
    /// <summary>Fallbacks for the very first layout pass only, before the bubble has ever been measured.</summary>
    private const double CaptionSeedW = 430, CaptionSeedH = 150;

    /// <summary>The bubble's real rendered size, written by the view from the caption border's own bounds;
    /// captions with a detail paragraph exceed 200px, so a fixed guess clips them off screen.</summary>
    [ObservableProperty] private double _guideCaptionW, _guideCaptionH;

    /// <summary>Bounds of the card a Below-placed bubble hangs from, in overlay coordinates, written by
    /// the view. Zero when the step names no anchor.</summary>
    [ObservableProperty] private double _guideAnchorX, _guideAnchorY, _guideAnchorW, _guideAnchorH;

    /// <summary>The control the current step wants the bubble anchored under, if any.</summary>
    public string GuideAnchorName => GuideStep?.AnchorName ?? "";

    private double CapW => GuideCaptionW > 0 ? GuideCaptionW : CaptionSeedW;
    private double CapH => GuideCaptionH > 0 ? GuideCaptionH : CaptionSeedH;

    /// <summary>Caption placement: offset from the measured hole, clamped to the measured viewport.
    /// Right of the target when it fits, left when it does not, never off any edge.</summary>
    public Thickness GuideCaptionMargin
    {
        get
        {
            if (!GuideHasTarget)
            {
                // A stop that DECLARES no target (the sign-off) centers, like the Welcome card. A stop whose
                // target merely failed to RESOLVE this frame keeps the card exactly where it was: during a
                // download the arrow is replaced by a progress state for a moment, and centring in that gap
                // made the card leap to mid-screen and back too fast to read.
                if (GuideStep is { TargetName.Length: > 0 }) return _lastCaptionMargin;

                double cx = GuideViewportW > 0 ? (GuideViewportW - CapW) / 2 : CaptionEdge;
                double cy = GuideViewportH > 0 ? (GuideViewportH - CapH) / 2 : CaptionEdge;
                return new(Math.Max(CaptionEdge, cx), Math.Max(CaptionEdge, cy), 0, 0);
            }

            // PARKED placement (Overview): the bubble lands over the activity card so it cannot cover the
            // coverage figures and health verdict the step tells the user to read. Normal rules until measured.
            if (GuideStep is { Park: true } && GuideAnchorW > 0)
            {
                double px = GuideViewportW - CapW - CaptionEdge;
                if (GuideViewportW > 0) px = Math.Min(px, GuideViewportW - CapW - CaptionEdge);
                px = Math.Max(CaptionEdge, px);
                double py = GuideAnchorY + CaptionEdge;
                if (GuideViewportH > 0) py = Math.Min(py, GuideViewportH - CapH - CaptionEdge);
                py = Math.Max(CaptionEdge, py);
                return _lastCaptionMargin = new(px, py, 0, 0);
            }

            // BELOW placement, for targets inside a dialog: beside-the-window would cover the dialog itself.
            if (GuideStep is { Below: true })
            {
                // Anchor to the CARD when named, so the bubble clears the whole dialog; the hole alone would
                // tuck it under the OK button and still overlap the card.
                double right  = GuideAnchorW > 0 ? GuideAnchorX + GuideAnchorW : GuideHoleX + GuideHoleW;
                double bottom = GuideAnchorH > 0 ? GuideAnchorY + GuideAnchorH : GuideHoleY + GuideHoleH;

                double bx = right - CapW;
                if (GuideViewportW > 0) bx = Math.Min(bx, GuideViewportW - CapW - CaptionEdge);
                bx = Math.Max(CaptionEdge, bx);
                double by = bottom + CaptionGap;
                if (GuideViewportH > 0) by = Math.Min(by, GuideViewportH - CapH - ScanBarGap);
                by = Math.Max(CaptionEdge, by);
                return _lastCaptionMargin = new(bx, by, 0, 0);
            }

            // Side choice: measure both gaps and take the bigger one, not "right unless it cannot fit".
            double roomLeft  = GuideHoleX - CaptionEdge;
            double roomRight = GuideViewportW - (GuideHoleX + GuideHoleW) - CaptionEdge;
            double x = roomRight >= roomLeft
                ? GuideHoleX + GuideHoleW + CaptionGap               // park to the right
                : GuideHoleX - CaptionGap - CapW;                    // park to the left
            // Clamp BOTH ends: left placement can go negative, right placement can overhang a narrow window.
            if (GuideViewportW > 0) x = Math.Min(x, GuideViewportW - CapW - CaptionEdge);
            x = Math.Max(CaptionEdge, x);

            // Align to the target, sliding up only as far as needed to keep the whole bubble on screen.
            double y = Math.Max(CaptionEdge, GuideHoleY - 8);
            // Bottom clearance matches ScanBarGap above, so the bar/bubble stack reads as evenly spaced.
            if (GuideViewportH > 0) y = Math.Min(y, GuideViewportH - CapH - ScanBarGap);
            y = Math.Max(CaptionEdge, y);
            return _lastCaptionMargin = new(x, y, 0, 0);
        }
    }

    /// <summary>Where the caption last sat beside a resolved target, held so a target that VANISHES for a
    /// frame (a download arrow becoming a progress state) leaves the card in place instead of letting it
    /// leap to the no-target fallback and back.</summary>
    private Thickness _lastCaptionMargin = new(CaptionEdge, CaptionEdge, 0, 0);

    /// <summary>The scan bar sits directly above the caption and spans from the caption's left edge to the
    /// spotlit button's right edge; both ends are measured, so it tracks the layout.</summary>
    private const double ScanBarSeedH = 64, ScanBarGap = 10;

    /// <summary>The bar's real height, written by the view; an estimate leaves a gap between bar and bubble.</summary>
    [ObservableProperty] private double _guideScanBarH;
    private double BarH => GuideScanBarH > 0 ? GuideScanBarH : ScanBarSeedH;

    partial void OnGuideScanBarHChanged(double value) => OnPropertyChanged(nameof(GuideScanBarMargin));

    public Thickness GuideScanBarMargin
    {
        get
        {
            var cap = GuideCaptionMargin;
            double y = Math.Max(CaptionEdge, cap.Top - BarH - ScanBarGap);
            return new(cap.Left, y, 0, 0);
        }
    }

    public double GuideScanBarWidth
    {
        get
        {
            double left = GuideCaptionMargin.Left;
            double right = Math.Max(GuideHoleX + GuideHoleW, left + CapW);
            return Math.Max(240, right - left);
        }
    }

    partial void OnGuideAnchorXChanged(double value) => OnPropertyChanged(nameof(GuideCaptionMargin));
    partial void OnGuideAnchorYChanged(double value) => OnPropertyChanged(nameof(GuideCaptionMargin));
    partial void OnGuideAnchorWChanged(double value) => OnPropertyChanged(nameof(GuideCaptionMargin));
    partial void OnGuideAnchorHChanged(double value) => OnPropertyChanged(nameof(GuideCaptionMargin));
    partial void OnGuideCaptionWChanged(double value) => OnPropertyChanged(nameof(GuideCaptionMargin));
    partial void OnGuideCaptionHChanged(double value) => OnPropertyChanged(nameof(GuideCaptionMargin));

    partial void OnGuideViewportWChanged(double value) => OnPropertyChanged(nameof(GuideCaptionMargin));
    partial void OnGuideViewportHChanged(double value) => OnPropertyChanged(nameof(GuideCaptionMargin));

    partial void OnGuideHoleXChanged(double value) => RaiseHoleGeometry();
    partial void OnGuideHoleYChanged(double value) => RaiseHoleGeometry();
    partial void OnGuideHoleWChanged(double value) => RaiseHoleGeometry();
    partial void OnGuideHoleHChanged(double value) => RaiseHoleGeometry();
    partial void OnGuideHasTargetChanged(bool value)
    {
        RaiseHoleGeometry();
        // The caption's PLACEMENT depends on whether there is a target at all (no target centers it), so the
        // margin has to re-raise the moment that flips, not only when the stop changes.
        OnPropertyChanged(nameof(GuideCaptionMargin));
    }
    private void RaiseHoleGeometry()
    {
        OnPropertyChanged(nameof(GuideHoleTopMargin)); OnPropertyChanged(nameof(GuideHoleRightMargin));
        OnPropertyChanged(nameof(GuideHoleBottomMargin)); OnPropertyChanged(nameof(GuideHoleLeftMargin));
        OnPropertyChanged(nameof(GuideShowScrim));
        OnPropertyChanged(nameof(GuideCaptionMargin));
        OnPropertyChanged(nameof(GuideScanBarMargin)); OnPropertyChanged(nameof(GuideScanBarWidth));
    }

    /// <summary>The account step has two targets: with no account it rings the add-account invitation; walked
    /// back to with an account connected it points at the account produced, showing the result instead.</summary>
    public string GuideTargetName =>
        GuideStep is { Key: "add-account" } && _host.IsConnected ? "GuideAccountList"
        : GuideStep?.TargetName ?? "";
    /// <summary>Ancestor to search inside, when the target's name is ambiguous (a templated control).</summary>
    public string GuideScopeName => GuideStep?.ScopeName ?? "";
    /// <summary>Whether to dim the page. False on WAIT steps: nothing to single out, and the scrim would
    /// hide the progress the user is watching.</summary>
    public bool GuideShowScrim => GuideHasTarget && GuideStep is { NoScrim: false };
    public string GuideGroup   => GuideStep?.Title ?? "";
    /// <summary>Same two states as the target: an instruction going forward, a confirmation going back.</summary>
    public string GuideCaption =>
        GuideStep is { Key: "add-account" } && _host.IsConnected
            ? "Already done - this is the [Account] Grog backs up. You can add another here later."
            : GuideStep?.Caption ?? "";
    /// <summary>The detail body, with {protection}/{unlock} resolved from the host's token-protection
    /// snapshot, so the sign-in reassurance describes what this machine actually provides.</summary>
    public string GuideDetail => GuideStep is { } d
        ? d.Detail
              .Replace("{protection}", Grog.Core.Platform.OsKeyring.Describe(TokenProtection))
              .Replace("{unlock}", Grog.Core.Platform.OsKeyring.UnlockNote(TokenProtection))
        : "";

    /// <summary>FileTokenStore.Protection is a keyring probe on Linux/macOS and this getter is evaluated on
    /// every binding refresh: read the main window's snapshot (taken on the thread pool and re-raised through
    /// RaiseDetail when it changes); the live probe is only the fallback for a host that is not the main
    /// window (UI-thread sweep 09-06 r2).</summary>
    private Grog.Core.Platform.TokenProtection TokenProtection
        => _host is MainWindowViewModel vm ? vm.Accounts.TokenProtectionSnapshot : Grog.Core.Auth.FileTokenStore.Protection;
    public bool HasGuideDetail => GuideDetail.Length > 0;
    public string GuideDetailTitle => GuideStep?.DetailTitle ?? "";
    public bool HasGuideDetailTitle => GuideDetailTitle.Length > 0;
    /// <summary>The bubble's title line: counter plus title. Reporting stops (the confirmation, the sign-off)
    /// carry a title and no number, so a card that asks nothing cannot read as unfinished work.</summary>
    public string GuideCounter => GuideStep is { } st
        ? Grog.Core.Guide.FirstRunTour.IsCounted(st.Key)
            ? $"Step {Grog.Core.Guide.FirstRunTour.NumberOf(st.Key)} of {Grog.Core.Guide.FirstRunTour.Total}"
              + $" - {st.Title}"
            : st.Title
        : "";

    /// <summary>Exit says DONE only on the LAST card. It used to say Done from setup-done onwards, which made
    /// the finish-line card read as the ending and the two stops after it look like an appendix; the tour is
    /// one continuous walk now (owner decision 2026-08-24).</summary>
    public string GuideExitLabel => GuideStep?.Key == "farewell" ? "Done" : "Exit Wizard";

    /// <summary>On the sign-off card Done is THE action, so it takes the filled amber primary tier; on
    /// every earlier card Exit is an escape hatch and stays a quiet ghost.</summary>
    public bool GuideExitIsPrimary => GuideStep?.Key == "farewell";

    /// <summary>Assistant-only: pin the guide to one step so the render harness can walk every step's caption
    /// and prove each fits on screen. Null in the app; never set by the UI.</summary>
    internal string? GuideStepOverride;

    /// <summary>The cursor over the tour. Back and Next move it; app state only ever advances it past stops
    /// whose work is already done. There is no separate history to keep in step with reality, which is what
    /// made the old backward path disagree with the forward one.</summary>
    private readonly Grog.Core.Guide.GuideSession _guideSession = new();

    /// <summary>The stop to draw, already resolved to a face (open-this-page, doing, busy, landed). Null only
    /// when the guide is not running -- the cursor always points at a real stop.</summary>
    private Grog.Core.Guide.GuideView? GuideStep =>
        GuideActive ? _guideSession.Current(GuideSnapshot()) : null;

    /// <summary>Back is ALWAYS offered: off the front of the tour it reopens the Welcome card, so the whole
    /// run is walkable end to end in both directions.</summary>
    public bool CanGuideBack => GuideActive;

    /// <summary>Next keeps its place on every stop that could offer it -- disabled, not hidden, while waiting
    /// on a fact. Exception: a GATE stop hides it, since nothing past the login makes sense without an account.</summary>
    public bool ShowGuideNext => GuideActive
                                 && _guideSession.CanNext
                                 && !(GuideStep is { Gate: true } && !_guideSession.CanSkip(GuideSnapshot()));

    /// <summary>The tip changes with the state, so a disabled Next reads as waiting rather than broken.</summary>
    public string GuideNextTip => CanGuideNext ? "Keep this and carry on" : "Finish this step to continue";

    /// <summary>Next is offered when there is a stop ahead and this one may be left: always when retracing,
    /// and on the live edge when the stop is optional or its work is done.</summary>
    public bool CanGuideNext => GuideActive
                                && _guideSession.CanNext
                                && _guideSession.CanSkip(GuideSnapshot());

    /// <summary>Let app state carry the tour forward. Only ever advances, and only moves what the user is
    /// looking at when they were already at the live edge -- so reading an earlier stop is never interrupted
    /// by something finishing in the background.</summary>
    private bool NoteTrail()
    {
        if (!GuideActive) return false;
        // A pinned stop stays pinned: the render harness walks every stop by key, and letting state advance
        // the cursor underneath it made the harness measure a DIFFERENT stop than the one it asked for.
        if (GuideStepOverride is { Length: > 0 }) return false;
        bool moved = _guideSession.Sync(GuideSnapshot());
        if (moved) SaveGuideAcks();
        return moved;
    }

    /// <summary>Both Back and Next hand back the page the stop under the cursor lives on. Applying it is what
    /// makes the two directions symmetric: the old code synced only on history walks, so going back left a
    /// past caption sitting over the present page.</summary>
    private void ApplyGuideMove(Grog.Core.Guide.GuideSession.Move move)
    {
        if (move.ScaffoldFolder) _host.ScaffoldChosenFolder();
        // Through Navigate, NOT a bare CurrentView assignment: each page's arrival work lives there
        // (CloudSaves re-reads the manifest, Drives rebuilds inventory). Assigning the view directly left
        // the page unswitched on some walks -- Back showed the Cloud Saves card over the Settings page.
        if (move.NavigateTo is { Length: > 0 } dest && dest != _host.CurrentViewName)
            _host.Navigate(dest);
    }


    /// <summary>Set when Back walked off the front of the tour into the Welcome card, so "Show me around"
    /// RESUMES at the same stop instead of resetting the tour the way a genuine re-run does.</summary>
    internal bool WelcomeReopenedFromGuide;

    /// <summary>One stop back, and the page goes with it. Off the front of the tour, Back reopens the
    /// Welcome card -- the tour's true first screen -- pausing the guide (the welcome scrim swallows
    /// clicks, so both must never be up at once).</summary>
    [RelayCommand]
    private void GuideBack()
    {
        if (!GuideActive) return;
        if (!_guideSession.CanBack)
        {
            WelcomeReopenedFromGuide = true;
            GuideActive = false;
            _host.ReopenWelcome();
            return;
        }
        ApplyGuideMove(_guideSession.Back(GuideSnapshot()));
        SaveGuideAcks();
        RaiseGuide();
    }

    /// <summary>One stop forward. Retracing simply moves the cursor; on the live edge it also acknowledges
    /// this stop, which is how an optional one is skipped. Either way the page follows.</summary>
    [RelayCommand]
    private void GuideNext()
    {
        if (!GuideActive) return;
        if (!_guideSession.CanNext || !_guideSession.CanSkip(GuideSnapshot())) return;
        ApplyGuideMove(_guideSession.Next(GuideSnapshot()));
        SaveGuideAcks();
        RaiseGuide();
    }

    private Grog.Core.Guide.GuideState GuideSnapshot() => new(
        HasAccount: _host.IsConnected,
        HasLibrary: _host.TotalCount > 0,
        HasBackedUpAnything: _host.HasBackedUpAnything,
        CurrentView: _host.CurrentViewName,
        IsScanning: _host.IsScanning,
        // RunActive ALONE: PendingTaskCount is "how much is not backed up", which after a first scan is the
        // whole library -- work to do is not work started.
        RunStarted: _host.RunActive,
        ShowingItemCount: _host.ShowingItemCount,
        ScanRan: _guideScanRan,
        ItemCountSeen: _guideSawItemCount,
        Acked: _guideSession.Acked,
        // No pick means nothing was left to back up when the stop was reached: that counts as landed too.
        FirstFileBackedUp: _demoPick is { } dp && _host.IsFileBackedUp(dp.GogId, dp.FileKey),
        HasCloudSaves: _host.HasCloudSaves,
        // Once a demo file was chosen this tour it stays the demo (a revisit confirms THAT file, and the stop
        // stays in history even when nothing else is left); before one is chosen, ask the picker.
        HasDemoFile: _demoPick is not null || DemoCandidate is not null);

    /// <summary>The demo file for this tour: chosen on arrival at the back-up stop, kept until Exit/Start so
    /// walking Back to the stop confirms the same file instead of picking another. <see cref="GuideFilePick"/>
    /// is the LIVE pick (cleared when the stop is left, because the primary button's label follows it).</summary>
    private (long GogId, string FileKey)? _demoPick;

    /// <summary>The picker's answer, computed at most once per RaiseGuide pass: a snapshot is taken ~20 times
    /// per pass and RaiseGuide runs once a second during a backup, so the O(files) pick must not repeat.</summary>
    private (long GogId, string FileKey)? DemoCandidate => _demoCandidateFresh ? _demoCandidate : (_demoCandidate = CandidateDemoFile(), _demoCandidateFresh = true).Item1;
    private (long GogId, string FileKey)? _demoCandidate;
    private bool _demoCandidateFresh;

    // LATCHES, not live reads: both answer "did this happen at some point during the tour", and the whole
    // reason they exist is that the live values are false in the gap between a scan finishing and its summary
    // appearing -- which is exactly when the cursor used to slip past the summary stop.
    private bool _guideScanRan, _guideSawItemCount;

    /// <summary>Load the persisted acknowledgements; acks must be stored or the tour rewinds to a step
    /// already dealt with. Cleared on restart from Settings so a re-run starts fresh.</summary>
    private void LoadGuideAcks()
        => _guideSession.Load(_host.Settings.GuideAcked, _host.Settings.GuidePosition);

    private void SaveGuideAcks()
    {
        _host.Settings.GuideAcked = _guideSession.Acked.ToList();
        // The cursor position replaces the old trail: with one ordered list there is nothing to record but
        // where the user is, and a restart resumes on the same stop instead of re-deriving one.
        _host.Settings.GuidePosition = _guideSession.Position;
        try { _host.Settings.SaveSoon(); } catch { /* a failed save must not break the tour */ }
    }

    /// <summary>The one file the back-up step points at, held as GogId + FileKey, NOT the row object:
    /// RaiseDetail rebuilds DetailFiles on every refresh, so a stored row instance goes stale.</summary>
    public (long GogId, string FileKey)? GuideFilePick { get; private set; }

    /// <summary>Choose the first thing to back up and select it: smallest in-scope game file of the smallest
    /// game, so the demo download finishes while the user watches. Scope comes from the same predicate the
    /// queue and health tally use; extras are skipped on purpose.</summary>
    private (long GogId, string FileKey)? CandidateDemoFile()
    {
        // One queue snapshot per pick, not one lock per file.
        var queued = _host.QueuedFiles();
        return Grog.Core.Guide.GuideFilePicker.Pick(
            _host.Games.Where(g => g.Item is not null).Select(g => g.Item!), _host.EffectiveScope,
            alreadyInFlight: (gid, f) => queued.Contains((gid, f.FileKey)));
    }

    private void PrepareGuideFilePick()
    {
        // The policy lives in Core: Grog.Core.Guide.GuideFilePicker owns the selection rule. This method
        // only feeds it the rows the grid shows and applies the result to the UI.
        // Read the model, do not drive the UI: the picker reads items straight off the rows rather than
        // selecting each game to build its scoped file list.
        var best = DemoCandidate;
        // Select the game the file belongs to, so row highlight, detail pane and spotlit arrow all point at
        // one game. ONE selection change, not one per game in the library.
        if (best is { } pick) _host.SelectedRow = _host.Games.FirstOrDefault(g => g.GogId == pick.GogId) ?? _host.SelectedRow;
        GuideFilePick = best;   // null: nothing left to back up
        _demoPick = best;
    }

    /// <summary>Re-evaluate the tour against the host's current facts and re-raise every readout. The host
    /// calls this from its own state raise; Back/Next/Start call it themselves.</summary>
    public void Refresh() => RaiseGuide();

    /// <summary>Resume a tour in progress (launch mid-tour, or the Welcome card re-entered from Back): the
    /// persisted cursor, then the same activation pass a fresh start takes.</summary>
    public void Resume() => Begin(fresh: false);

    /// <summary>THE activation path. Start and Resume differ only in where the cursor comes from; everything
    /// after that (state sync, file pick, page reconciliation, raises) is one pass, so neither can forget a
    /// step the other does. Resume did exactly that before 09-02: it switched the guide on wherever the app
    /// had opened and drew a Library stop over the Overview.</summary>
    private void Begin(bool fresh)
    {
        if (fresh)
        {
            _host.Settings.FirstRunGuideDone = false;
            _host.Settings.SaveSoon();
            _guideScanRan = false;
            _guideSawItemCount = false;
            _demoPick = null;
            // "Show me around" is a genuine re-run: drop the stored acks so the tour starts from the beginning
            // of what is still relevant rather than resuming where the last one stopped. A re-run starts at
            // stop one, every time; Sync walks it forward past whatever is genuinely already done.
            _guideSession.Reset();
            if (GuideStepOverride is { Length: > 0 } pinned) _guideSession.GoTo(pinned);
            SaveGuideAcks();
        }
        else LoadGuideAcks();
        GuideActive = true;
        RaiseGuide(activating: true);
        SaveGuideAcks();   // a resume whose first Sync advanced the cursor is persisted now, not on the next move
    }

    /// <summary>Test seams: the invariant walk in GuideViewModelTests reads the cursor and the snapshot.</summary>
    internal Grog.Core.Guide.GuideSession SessionForTests => _guideSession;
    internal Grog.Core.Guide.GuideState SnapshotForTests() => GuideSnapshot();

    /// <summary>The key of the stop under the cursor, or null when the guide is off.</summary>
    public string? CurrentStepKey => GuideStep?.Key;

    /// <summary>Whether the named stop has been acknowledged (the rail's status decorations wait on one).</summary>
    public bool IsAcked(string key) => _guideSession.IsAcked(key);

    /// <summary>Accounts changed: the sign-in reassurance in the detail body may read differently.</summary>
    public void RaiseDetail() => OnPropertyChanged(nameof(GuideDetail));

    /// <summary>The row the detail pane keeps showing while the back-up step's file downloads, so the pane
    /// does not fall back to "first game" the moment the selection is released.</summary>
    public GameRowViewModel? DetailKeep { get; set; }

    /// <summary>Re-entrancy guard for the page reconciliation: Navigate raises host state, which calls back
    /// into Refresh; the nested pass must not navigate again.</summary>
    private bool _reconciling;

    private void RaiseGuide(bool activating = false)
    {
        if (!GuideActive) return;
        _demoCandidateFresh = false;   // one picker run per pass, at most

        // Latch the two scan facts FIRST, before Sync reads them: a scan that has started must stay
        // "ran" after CatalogBusy goes false, or the summary stop self-completes in the gap before its
        // dialog appears and the tour walks on without it.
        if (_host.IsScanning) _guideScanRan = true;
        if (_host.ShowingItemCount) _guideSawItemCount = true;

        // Let state carry the tour forward. Nothing here can move the cursor BACK, so a user reading an
        // earlier stop keeps reading it.
        bool moved = NoteTrail();

        // INVARIANT: whenever the cursor has just landed on a stop (state moved it, or the tour is being
        // switched on), the window is on that stop's page. Back and Next enforce this through their own Move;
        // this is the same rule for the other two ways a cursor can arrive somewhere. Manual navigation AWAY
        // is respected: the cursor does not follow the user, and the user is not yanked back.
        if ((moved || activating) && !_reconciling)
        {
            _reconciling = true;
            int landedAt = _guideSession.Position;
            try { ApplyGuideMove(_guideSession.Landing(GuideSnapshot())); }
            finally { _reconciling = false; }
            // The navigation re-entered this method (Navigate -> RaiseState -> Refresh) with the guard up; if
            // that nested pass moved the cursor again, reconcile once more so the page matches the final stop.
            if (_guideSession.Position != landedAt) ApplyGuideMove(_guideSession.Landing(GuideSnapshot()));
        }

        // The back-up stop needs its file chosen BEFORE the overlay measures, or the first layout pass has
        // nothing to ring. Runs once per arrival (re-picking would fight the user's own selection) and
        // releases the pick once the stop is behind us, so the primary reads the whole library again
        // instead of the one demo file.
        var stepNow = GuideStep?.Key;
        if (stepNow == "back-up") { if (GuideFilePick is null) PrepareGuideFilePick(); }
        else if (GuideFilePick is not null)
        {
            // Leaving the backup stop releases BOTH the pick and the grid selection it made: the primary
            // button's label follows the selection, and "Back up - 1 item" directly contradicted the card
            // that just said the button queues the whole library. Guarded on the pick so a selection the
            // USER makes later is never cleared by a guide re-render.
            GuideFilePick = null;
            _host.ClearGridSelection();
        }



        OnPropertyChanged(nameof(GuideTargetName)); OnPropertyChanged(nameof(GuideScopeName));
        OnPropertyChanged(nameof(GuideShowScrim));
        OnPropertyChanged(nameof(GuideCaptionMargin));
        OnPropertyChanged(nameof(GuideGroup));
        OnPropertyChanged(nameof(GuideCaption));    OnPropertyChanged(nameof(GuideCounter));
        OnPropertyChanged(nameof(GuideDetail));     OnPropertyChanged(nameof(HasGuideDetail));
        OnPropertyChanged(nameof(GuideDetailTitle)); OnPropertyChanged(nameof(HasGuideDetailTitle));
        OnPropertyChanged(nameof(CanGuideBack));    OnPropertyChanged(nameof(CanGuideNext));
        OnPropertyChanged(nameof(ShowGuideNext)); OnPropertyChanged(nameof(GuideNextTip));
        OnPropertyChanged(nameof(GuideExitLabel)); OnPropertyChanged(nameof(GuideExitIsPrimary));
        // The host's step-dependent readouts (primary label, rail status trio) follow through Changed.
        Changed?.Invoke();

        // Completion is acking the LAST stop, nothing else. It used to be a state predicate, which is how a
        // tour could end before showing its final card.
        if (_guideSession.IsComplete) ExitGuide();
    }

    /// <summary>Raised whenever the step or the active flag moved: the host re-raises everything of its own
    /// that reads the guide (primary label, rail status, the scan panel's page/overlay swap).</summary>
    public event Action? Changed;

    partial void OnGuideActiveChanged(bool value) => Changed?.Invoke();

    [RelayCommand]
    private void ExitGuide()
    {
        // Leaving the tour settles the folder question by default, so the structure gets built here too;
        // scaffolding must not depend on reaching one particular step.
        _host.ScaffoldChosenFolder();
        DetailKeep = null;   // the pane goes back to its normal fallback once the tour is over
        // Release the demo pick with the tour: a stale one would clear a later user selection on re-run.
        if (GuideFilePick is { } fp && _host.SelectedRow is { } sel && sel.GogId == fp.GogId) _host.SelectedRow = null;
        GuideFilePick = null; _demoPick = null;
        GuideActive = false;
        _host.Settings.FirstRunGuideDone = true;
        _host.Settings.GuideAcked = _guideSession.Acked.ToList();   // keep the position: Exit is not necessarily final
        _host.Settings.SaveSoon();
        _host.Log("Setup guide closed. Reopen it any time from Settings.");
    }

    [RelayCommand]
    private void StartGuide() => Begin(fresh: true);
}
