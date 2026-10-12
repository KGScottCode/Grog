// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.LogicalTree;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Grog.App.ViewModels;
using Grog.Core.Layout;

namespace Grog.App.Views;

// Guided tour: spotlight measurement, caption painting, target resolution.
public partial class MainWindow
{
    private Panel? _guideOverlay;
    private TextBlock? _guideCaptionText, _guideDetailText;

    private GuideViewModel? _guideSubscribed;
    private bool _guideDirty = true;
    private bool _guideCachedActive;
    private string _guideCachedTargetName = "", _guideCachedScopeName = "", _guideCachedAnchorName = "";
    private string? _guideCachedCaption, _guideCachedDetail;
    private (long GogId, string FileKey)? _guideCachedFilePick;
    private Control? _guideTargetCtl; private string _guideTargetCtlKey = "";
    private Control? _guideCaptionBox, _guideScanBar, _guideAnchorCard; private string _guideAnchorCardName = "";

    private void EnsureGuideSubscription(MainWindowViewModel vm)
    {
        if (ReferenceEquals(_guideSubscribed, vm.Guide)) return;
        if (_guideSubscribed is not null) _guideSubscribed.PropertyChanged -= OnGuidePropertyChanged;
        _guideSubscribed = vm.Guide;
        _guideSubscribed.PropertyChanged += OnGuidePropertyChanged;
        _guideDirty = true;
    }

    private void OnGuidePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        var n = e.PropertyName ?? "";
        if (n.EndsWith("Margin", StringComparison.Ordinal)   // derived placement values, recomputed from the geometry below
            || n.StartsWith("GuideHole", StringComparison.Ordinal) || n.StartsWith("GuideViewport", StringComparison.Ordinal)
            || n.StartsWith("GuideAnchor", StringComparison.Ordinal) && n != nameof(GuideViewModel.GuideAnchorName)
            || n is nameof(GuideViewModel.GuideCaptionW) or nameof(GuideViewModel.GuideCaptionH)
                 or nameof(GuideViewModel.GuideScanBarH) or nameof(GuideViewModel.GuideHasTarget))
            return;
        _guideDirty = true;
    }

    private void RefreshGuideCache(MainWindowViewModel vm)
    {
        if (!_guideDirty) return;
        _guideDirty = false;
        _guideCachedActive = vm.Guide.GuideActive;
        if (!_guideCachedActive) { _guideTargetCtl = null; _guideTargetCtlKey = ""; return; }
        var target = vm.Guide.GuideTargetName;
        var scope = vm.Guide.GuideScopeName;
        var pick = vm.Guide.GuideFilePick;
        var key = scope + "|" + target + "|" + (pick is { } pk ? pk.GogId + ":" + pk.FileKey : "");
        if (key != _guideTargetCtlKey) { _guideTargetCtl = null; _guideTargetCtlKey = key; }
        _guideCachedTargetName = target; _guideCachedScopeName = scope; _guideCachedFilePick = pick;
        _guideCachedAnchorName = vm.Guide.GuideAnchorName;
        _guideCachedCaption = vm.Guide.GuideCaption; _guideCachedDetail = vm.Guide.GuideDetail;
    }

    private static bool GuideCtlAlive(Control? c) => c is not null && c.IsEffectivelyVisible && c.IsAttachedToVisualTree();

    private void MeasureGuideTarget()
    {
        if (DataContext is not MainWindowViewModel vm) return;
        EnsureGuideSubscription(vm);
        RefreshGuideCache(vm);
        if (!_guideCachedActive) return;

        PrepareGuideBackupView(vm);
        PaintGuideText(vm);

        var name = _guideCachedTargetName;
        Control? target;
        if (string.IsNullOrEmpty(name)) target = null;
        else if (GuideCtlAlive(_guideTargetCtl) && GuideTargetStillMatches(_guideTargetCtl!, name)) target = _guideTargetCtl;
        else
            target = _guideTargetCtl = ResolveTarget(_guideCachedScopeName, name);

        if (target is null || target.Bounds.Width <= 0)
        {
            if (vm.Guide.GuideHasTarget) vm.Guide.GuideHasTarget = false;
            return;
        }

        if (target.FindAncestorOfType<ScrollViewer>() is { } sv
            && target.TranslatePoint(new Point(0, 0), sv) is { } tp)
        {
            double th = target.Bounds.Height;
            if (tp.Y < 0 || tp.Y + th > sv.Viewport.Height)
            {
                double newY = Math.Max(0, sv.Offset.Y + tp.Y - (sv.Viewport.Height - th) / 2);
                sv.Offset = sv.Offset.WithY(newY);
            }
        }

        var overlay = _guideOverlay ??= Find<Panel>("GuideOverlayRoot");
        if (overlay is null) { vm.Guide.GuideHasTarget = false; return; }
        if (target.TranslatePoint(new Point(0, 0), overlay) is not { } p) { vm.Guide.GuideHasTarget = false; return; }

        const double pad = 6;
        double x = Math.Max(0, p.X - pad), y = Math.Max(0, p.Y - pad);
        double w = target.Bounds.Width + (p.X - x) + pad, h = target.Bounds.Height + (p.Y - y) + pad;

        vm.Guide.GuideViewportW = overlay.Bounds.Width; vm.Guide.GuideViewportH = overlay.Bounds.Height;

        MeasureGuideCaption(vm);
        MeasureGuideAnchor(vm, overlay);

        if (Math.Abs(vm.Guide.GuideHoleX - x) < 0.5 && Math.Abs(vm.Guide.GuideHoleY - y) < 0.5
            && Math.Abs(vm.Guide.GuideHoleW - w) < 0.5 && Math.Abs(vm.Guide.GuideHoleH - h) < 0.5 && vm.Guide.GuideHasTarget)
            return;

        vm.Guide.GuideHoleX = x; vm.Guide.GuideHoleY = y; vm.Guide.GuideHoleW = w; vm.Guide.GuideHoleH = h;
        vm.Guide.GuideHasTarget = true;
    }

    private void MeasureGuideCaption(MainWindowViewModel vm)
    {
        var box = GuideCtlAlive(_guideCaptionBox) ? _guideCaptionBox
                : _guideCaptionBox = this.GetVisualDescendants().OfType<Control>()
                      .FirstOrDefault(c => c.Name == "GuideCaptionBox" && c.IsEffectivelyVisible);   // cached
        if (box is null || box.Bounds.Width <= 0 || box.Bounds.Height <= 0) return;
        if (Math.Abs(vm.Guide.GuideCaptionW - box.Bounds.Width) < 0.5
            && Math.Abs(vm.Guide.GuideCaptionH - box.Bounds.Height) < 0.5) return;
        vm.Guide.GuideCaptionW = box.Bounds.Width;
        vm.Guide.GuideCaptionH = box.Bounds.Height;
        MeasureGuideScanBar(vm);
    }

    private void MeasureGuideAnchor(MainWindowViewModel vm, Visual overlay)
    {
        var name = _guideCachedAnchorName;   // cached step value
        if (string.IsNullOrEmpty(name))
        {
            if (vm.Guide.GuideAnchorW != 0) { vm.Guide.GuideAnchorW = 0; vm.Guide.GuideAnchorH = 0; }
            return;
        }
        if (name != _guideAnchorCardName) { _guideAnchorCard = null; _guideAnchorCardName = name; }
        var card = GuideCtlAlive(_guideAnchorCard) ? _guideAnchorCard
                 : _guideAnchorCard = this.GetVisualDescendants().OfType<Control>()
                       .FirstOrDefault(c => c.Name == name && c.IsEffectivelyVisible);
        if (card is null || card.Bounds.Width <= 0) { vm.Guide.GuideAnchorW = 0; vm.Guide.GuideAnchorH = 0; return; }
        if (card.TranslatePoint(new Point(0, 0), overlay) is not { } cp) return;
        if (Math.Abs(vm.Guide.GuideAnchorX - cp.X) < 0.5 && Math.Abs(vm.Guide.GuideAnchorY - cp.Y) < 0.5
            && Math.Abs(vm.Guide.GuideAnchorW - card.Bounds.Width) < 0.5
            && Math.Abs(vm.Guide.GuideAnchorH - card.Bounds.Height) < 0.5) return;
        vm.Guide.GuideAnchorX = cp.X; vm.Guide.GuideAnchorY = cp.Y;
        vm.Guide.GuideAnchorW = card.Bounds.Width; vm.Guide.GuideAnchorH = card.Bounds.Height;
    }

    private void MeasureGuideScanBar(MainWindowViewModel vm)
    {
        var bar = GuideCtlAlive(_guideScanBar) ? _guideScanBar
                : _guideScanBar = this.GetVisualDescendants().OfType<Control>()
                      .FirstOrDefault(c => c.Name == "GuideScanBar" && c.IsEffectivelyVisible);   // cached
        if (bar is null || bar.Bounds.Height <= 0) return;
        if (Math.Abs(vm.Guide.GuideScanBarH - bar.Bounds.Height) < 0.5) return;
        vm.Guide.GuideScanBarH = bar.Bounds.Height;
    }

    private string _guideCaptionPainted = "\0", _guideDetailPainted = "\0";

    private void PaintGuideText(MainWindowViewModel vm)
    {
        Paint(_guideCaptionText ??= Find<TextBlock>("GuideCaptionText"), _guideCachedCaption, ref _guideCaptionPainted);   // cached step values
        Paint(_guideDetailText ??= Find<TextBlock>("GuideDetailText"), _guideCachedDetail, ref _guideDetailPainted);

        static void Paint(TextBlock? tb, string? text, ref string last)
        {
            if (tb is null) return;
            text ??= "";
            if (last == text) return;   // LayoutUpdated fires constantly; rebuilding inlines every pass would thrash
            last = text;

            tb.Inlines?.Clear();
            if (tb.Inlines is null) { tb.Text = text.Replace("[", "").Replace("]", ""); return; }

            foreach ((string part, bool isName) in Split(text))
                tb.Inlines.Add(isName
                    ? new Avalonia.Controls.Documents.Run(part)
                        { Foreground = Palette.AccentAmber, FontWeight = Avalonia.Media.FontWeight.SemiBold }
                    : new Avalonia.Controls.Documents.Run(part));
        }

        static System.Collections.Generic.IEnumerable<(string Text, bool IsName)> Split(string s)
        {
            int i = 0;
            while (i < s.Length)
            {
                int open = s.IndexOf('[', i);
                if (open < 0) { yield return (s[i..], false); yield break; }
                int close = s.IndexOf(']', open + 1);
                if (close < 0) { yield return (s[i..], false); yield break; }   // unclosed: leave it alone
                if (open > i) yield return (s[i..open], false);
                yield return (s[(open + 1)..close], true);
                i = close + 1;
            }
        }
    }

    private bool _guideBackupViewReady;

    private void PrepareGuideBackupView(MainWindowViewModel vm)
    {
        if (_guideCachedFilePick is null) { _guideBackupViewReady = false; return; }   // cached
        if (_guideBackupViewReady) return;

        var grid = Find<DataGrid>("LibraryGrid");
        if (grid is null || !grid.IsEffectivelyVisible) return;   // not on Library yet; try again next pass

        var sizeCol = grid.Columns.FirstOrDefault(c => c.SortMemberPath == "TotalSizeBytes");
        if (sizeCol is not null) sizeCol.Sort(System.ComponentModel.ListSortDirection.Ascending);
        if (vm.Library.SelectedRow is { } row) grid.ScrollIntoView(row, null);
        _guideBackupViewReady = true;
    }

    private bool GuideTargetStillMatches(Control c, string name)
    {
        if (name == "GuideFileDownload" || name == "GuideFileDone")
            return _guideCachedFilePick is { } pick && c.DataContext is DetailFileRow r
                && r.GogId == pick.GogId && r.File.FileKey == pick.FileKey;
        return true;
    }

    private Control? ResolveTarget(string scopeName, string name)
    {
        if (name == "GuideFileDownload")
        {
            if (_guideCachedFilePick is not { } pick) return null;
            return this.GetVisualDescendants().OfType<Button>()
                       .FirstOrDefault(b => b.IsEffectivelyVisible
                                         && b.Classes.Contains("filedl")
                                         && b.DataContext is DetailFileRow r
                                         && r.GogId == pick.GogId
                                         && r.File.FileKey == pick.FileKey);
        }

        if (name == "GuideFileDone")
        {
            if (_guideCachedFilePick is not { } donePick) return null;
            return this.GetVisualDescendants().OfType<TextBlock>()
                       .FirstOrDefault(t => t.IsEffectivelyVisible
                                         && t.Name == "GuideFileDone"
                                         && t.DataContext is DetailFileRow r
                                         && r.GogId == donePick.GogId
                                         && r.File.FileKey == donePick.FileKey);
        }

        Visual root = this;
        if (!string.IsNullOrEmpty(scopeName))
        {
            var scope = this.GetVisualDescendants().OfType<Control>()
                            .FirstOrDefault(c => c.Name == scopeName && c.IsEffectivelyVisible);
            if (scope is null) return null;   // scope off screen -> the target cannot be on it either
            root = scope;
        }
        return root.GetVisualDescendants().OfType<Control>()
                   .FirstOrDefault(c => c.Name == name && c.IsEffectivelyVisible);
    }
}
