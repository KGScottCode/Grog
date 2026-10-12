// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Linq;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Grog.App.ViewModels;

public sealed partial class GuideViewModel : ObservableObject
{
    private readonly IGuideHost _host;
    internal GuideViewModel(IGuideHost host) => _host = host;

    [ObservableProperty] private bool _guideActive;
    [ObservableProperty] private double _guideHoleX, _guideHoleY, _guideHoleW, _guideHoleH;
    [ObservableProperty] private bool _guideHasTarget;

    public Thickness GuideHoleTopMargin    => new(GuideHoleX, GuideHoleY, 0, 0);
    public Thickness GuideHoleLeftMargin   => new(0, GuideHoleY, 0, 0);
    public Thickness GuideHoleRightMargin  => new(GuideHoleX + GuideHoleW, GuideHoleY, 0, 0);
    public Thickness GuideHoleBottomMargin => new(0, GuideHoleY + GuideHoleH, 0, 0);
    [ObservableProperty] private double _guideViewportW, _guideViewportH;

    private const double CaptionGap = 18, CaptionEdge = 12;
    private const double CaptionSeedW = 430, CaptionSeedH = 150;

    [ObservableProperty] private double _guideCaptionW, _guideCaptionH;

    [ObservableProperty] private double _guideAnchorX, _guideAnchorY, _guideAnchorW, _guideAnchorH;

    public string GuideAnchorName => GuideStep?.AnchorName ?? "";

    private double CapW => GuideCaptionW > 0 ? GuideCaptionW : CaptionSeedW;
    private double CapH => GuideCaptionH > 0 ? GuideCaptionH : CaptionSeedH;

    public Thickness GuideCaptionMargin
    {
        get
        {
            if (!GuideHasTarget)
            {
                if (GuideStep is { TargetName.Length: > 0 }) return _lastCaptionMargin;

                double cx = GuideViewportW > 0 ? (GuideViewportW - CapW) / 2 : CaptionEdge;
                double cy = GuideViewportH > 0 ? (GuideViewportH - CapH) / 2 : CaptionEdge;
                return new(Math.Max(CaptionEdge, cx), Math.Max(CaptionEdge, cy), 0, 0);
            }

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

    private Thickness _lastCaptionMargin = new(CaptionEdge, CaptionEdge, 0, 0);

    private const double ScanBarSeedH = 64, ScanBarGap = 10;

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

    public string GuideTargetName =>
        GuideStep is { Key: "add-account" } && _host.IsConnected ? "GuideAccountList"
        : GuideStep?.TargetName ?? "";
    public string GuideScopeName => GuideStep?.ScopeName ?? "";
    public bool GuideShowScrim => GuideHasTarget && GuideStep is { NoScrim: false };
    public string GuideGroup   => GuideStep?.Title ?? "";
    public string GuideCaption =>
        GuideStep is { Key: "add-account" } && _host.IsConnected
            ? "Already done - this is the [Account] Grog backs up. You can add another here later."
            : GuideStep?.Caption ?? "";
    public string GuideDetail => GuideStep is { } d
        ? d.Detail
              .Replace("{protection}", Grog.Core.Platform.OsKeyring.Describe(TokenProtection))
              .Replace("{unlock}", Grog.Core.Platform.OsKeyring.UnlockNote(TokenProtection))
        : "";

    private Grog.Core.Platform.TokenProtection TokenProtection
        => _host is MainWindowViewModel vm ? vm.Accounts.TokenProtectionSnapshot : Grog.Core.Auth.FileTokenStore.Protection;
    public bool HasGuideDetail => GuideDetail.Length > 0;
    public string GuideDetailTitle => GuideStep?.DetailTitle ?? "";
    public bool HasGuideDetailTitle => GuideDetailTitle.Length > 0;
    public string GuideCounter => GuideStep is { } st
        ? Grog.Core.Guide.FirstRunTour.IsCounted(st.Key)
            ? $"Step {Grog.Core.Guide.FirstRunTour.NumberOf(st.Key)} of {Grog.Core.Guide.FirstRunTour.Total}"
              + $" - {st.Title}"
            : st.Title
        : "";

    public string GuideExitLabel => GuideStep?.Key == "farewell" ? "Done" : "Exit Wizard";

    public bool GuideExitIsPrimary => GuideStep?.Key == "farewell";

    internal string? GuideStepOverride;

    private readonly Grog.Core.Guide.GuideSession _guideSession = new();

    private Grog.Core.Guide.GuideView? GuideStep =>
        GuideActive ? _guideSession.Current(GuideSnapshot()) : null;

    public bool CanGuideBack => GuideActive;

    public bool ShowGuideNext => GuideActive
                                 && _guideSession.CanNext
                                 && !(GuideStep is { Gate: true } && !_guideSession.CanSkip(GuideSnapshot()));

    public string GuideNextTip => CanGuideNext ? "Keep this and carry on" : "Finish this step to continue";

    public bool CanGuideNext => GuideActive
                                && _guideSession.CanNext
                                && _guideSession.CanSkip(GuideSnapshot());

    private bool NoteTrail()
    {
        if (!GuideActive) return false;
        if (GuideStepOverride is { Length: > 0 }) return false;
        bool moved = _guideSession.Sync(GuideSnapshot());
        if (moved) SaveGuideAcks();
        return moved;
    }

    private void ApplyGuideMove(Grog.Core.Guide.GuideSession.Move move)
    {
        if (move.ScaffoldFolder) _host.ScaffoldChosenFolder();
        if (move.NavigateTo is { Length: > 0 } dest && dest != _host.CurrentViewName)
            _host.Navigate(dest);
    }


    internal bool WelcomeReopenedFromGuide;

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
        RunStarted: _host.RunActive,
        ShowingItemCount: _host.ShowingItemCount,
        ScanRan: _guideScanRan,
        ItemCountSeen: _guideSawItemCount,
        Acked: _guideSession.Acked,
        FirstFileBackedUp: _demoPick is { } dp && _host.IsFileBackedUp(dp.GogId, dp.FileKey),
        HasCloudSaves: _host.HasCloudSaves,
        HasDemoFile: _demoPick is not null || DemoCandidate is not null);

    private (long GogId, string FileKey)? _demoPick;

    private (long GogId, string FileKey)? DemoCandidate => _demoCandidateFresh ? _demoCandidate : (_demoCandidate = CandidateDemoFile(), _demoCandidateFresh = true).Item1;
    private (long GogId, string FileKey)? _demoCandidate;
    private bool _demoCandidateFresh;

    private bool _guideScanRan, _guideSawItemCount;

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

    public (long GogId, string FileKey)? GuideFilePick { get; private set; }

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
        var best = DemoCandidate;
        if (best is { } pick) _host.SelectedRow = _host.Games.FirstOrDefault(g => g.GogId == pick.GogId) ?? _host.SelectedRow;
        GuideFilePick = best;   // null: nothing left to back up
        _demoPick = best;
    }

    public void Refresh() => RaiseGuide();

    public void Resume() => Begin(fresh: false);

    private void Begin(bool fresh)
    {
        if (fresh)
        {
            _host.Settings.FirstRunGuideDone = false;
            _host.Settings.SaveSoon();
            _guideScanRan = false;
            _guideSawItemCount = false;
            _demoPick = null;
            _guideSession.Reset();
            if (GuideStepOverride is { Length: > 0 } pinned) _guideSession.GoTo(pinned);
            SaveGuideAcks();
        }
        else LoadGuideAcks();
        GuideActive = true;
        RaiseGuide(activating: true);
        SaveGuideAcks();   // a resume whose first Sync advanced the cursor is persisted now, not on the next move
    }

    internal Grog.Core.Guide.GuideSession SessionForTests => _guideSession;
    internal Grog.Core.Guide.GuideState SnapshotForTests() => GuideSnapshot();

    public string? CurrentStepKey => GuideStep?.Key;

    public bool IsAcked(string key) => _guideSession.IsAcked(key);

    public void RaiseDetail() => OnPropertyChanged(nameof(GuideDetail));

    public GameRowViewModel? DetailKeep { get; set; }

    private bool _reconciling;

    private void RaiseGuide(bool activating = false)
    {
        if (!GuideActive) return;
        _demoCandidateFresh = false;   // one picker run per pass, at most

        if (_host.IsScanning) _guideScanRan = true;
        if (_host.ShowingItemCount) _guideSawItemCount = true;

        bool moved = NoteTrail();

        if ((moved || activating) && !_reconciling)
        {
            _reconciling = true;
            int landedAt = _guideSession.Position;
            try { ApplyGuideMove(_guideSession.Landing(GuideSnapshot())); }
            finally { _reconciling = false; }
            if (_guideSession.Position != landedAt) ApplyGuideMove(_guideSession.Landing(GuideSnapshot()));
        }

        var stepNow = GuideStep?.Key;
        if (stepNow == "back-up") { if (GuideFilePick is null) PrepareGuideFilePick(); }
        else if (GuideFilePick is not null)
        {
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

        if (_guideSession.IsComplete) ExitGuide();
    }

    public event Action? Changed;

    partial void OnGuideActiveChanged(bool value) => Changed?.Invoke();

    [RelayCommand]
    private void ExitGuide()
    {
        _host.ScaffoldChosenFolder();
        DetailKeep = null;   // the pane goes back to its normal fallback once the tour is over
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
