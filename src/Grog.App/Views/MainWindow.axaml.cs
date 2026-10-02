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

public partial class MainWindow : Window
{
    /// <summary>Named-control lookup that crosses page boundaries. The pages are UserControls (09-02 split)
    /// with their own name scopes, so <c>FindControl</c> on the window no longer sees LibraryGrid or the
    /// queue lists; the logical tree does. Null until the page exists, exactly as before.</summary>
    private T? Find<T>(string name) where T : class
        => this.GetLogicalDescendants().OfType<T>().FirstOrDefault(c => (c as StyledElement)?.Name == name);

    private readonly AppSettings _settings = AppSettings.Load();

    // Fixed-size layout harness: the "app 1920x1080" / "app 1366x768" launch profiles set
    // GROG_FIXED_WINDOW=1 to force a pinned 1920x1080 startup (GROG_WINDOW_SIZE may then shrink it)
    // and skip size/position persistence, so layout checks never disturb the saved layout. Unset
    // (normal runs and the "app remembered window" profile) restores and persists the user's window.
    private static readonly bool FixedStartupWindow =
        System.Environment.GetEnvironmentVariable("GROG_FIXED_WINDOW") == "1";

    public MainWindow()
    {
        InitializeComponent();

        // Frameless chrome: own min/max/close drawn in XAML, OS caption removed. Set in code because the
        // enum attribute does not resolve in XAML; the window stays resizable and snappable.
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = 34;  // reserve exactly our bar height; no system-drawn title

        // On X11 the extend hint is only a hint and some WMs (Cinnamon's Muffin) keep their own frame, doubling
        // the controls; WindowDecorations.None is definitive, at the cost of WM snap and frame-edge resize.
        if (OperatingSystem.IsLinux())
        {
            WindowDecorations = WindowDecorations.None;   // Avalonia 12 name (SystemDecorations is obsolete)
            EnableEdgeResize();   // None removes the WM's resize borders too; these grips give them back
        }

        // macOS keeps its traffic lights in the extended client area, so the custom pair would duplicate
        // them - and the native ones are the pair that actually WORKS there (custom minimize bounced the
        // window straight back, custom close did nothing). The red light raises Closing like any close,
        // so tray/quit/confirm behavior is identical.
        if (OperatingSystem.IsMacOS())
            Find<Avalonia.Controls.StackPanel>("CustomCaptionButtons")!.IsVisible = false;

        RestoreWindowSize();

        // Window icon follows the accent theme live (title bar + taskbar entry); the static launcher/exe
        // icon stays the amber default.
        ApplyThemeIcon(ThemeService.Current);
        ApplyAboutScene(ThemeService.Current);
        ThemeService.Changed += OnThemeChanged;
        Closed += (_, _) => ThemeService.Changed -= OnThemeChanged;

        // First-run guide: re-measure the spotlight on every layout pass -- the layout can move the target
        // (resize, rail collapse, scroll) independently of step changes.
        LayoutUpdated += (_, _) => MeasureGuideTarget();
        // Coming back by ANY route (tray click, taskbar, alt-tab) clears the missed-notification paint.
        Activated += (_, _) => ResetTrayAttention();

        // macOS: restoring a minimized window can come back with rendering STOPPED (upstream
        // AvaloniaUI/Avalonia#18148, open) - the window reappears empty. Nudge the visual tree on
        // every activation there; a no-op when rendering is healthy.
        if (OperatingSystem.IsMacOS())
        {
            Activated += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                InvalidateMeasure();
                (Content as Control)?.InvalidateVisual();
            });
            PropertyChanged += (_, e) =>
            {
                if (e.Property != WindowStateProperty) return;
                // Deminiaturize fighting a deliberate hide. An IMMEDIATE Hide() is silently ignored while
                // the window is mid-animation (upstream #12971: a minimized window cannot be hidden), so
                // wait for the transitions to stop: each one restarts a short timer, and only the timer's
                // expiry - the animation having settled - actually hides. A second check catches a late
                // orderFront that arrives with no state event at all.
                if (DateTime.UtcNow < _macRehideUntil && e.NewValue is WindowState.Normal or WindowState.Maximized)
                {
                    var scheduledFor = DateTime.UtcNow;
                    DispatcherTimer.RunOnce(() =>
                    {
                        if (DateTime.UtcNow >= _macRehideUntil) return;
                        Hide();
                        DispatcherTimer.RunOnce(() =>
                        {
                            if (DateTime.UtcNow < _macRehideUntil && IsVisible)
                            Hide();
                        }, TimeSpan.FromMilliseconds(500));
                    }, TimeSpan.FromMilliseconds(400));
                }
            };

            // MINIMIZE IS BROKEN UPSTREAM on this backend: the window bounces back (#13002's state flap)
            // and, when it does stay down, restores with a dead renderer (#18148) - a blank canvas.
            // Verified on hardware from BOTH normal and maximized states (MacinCloud 2026-08-28). So on
            // macOS minimize becomes HIDE (the Cmd+H idiom every mac user knows), and a Dock click
            // (ActivationKind.Reopen) brings the window back through Show(), which renders correctly.
            PropertyChanged += (_, e) =>
            {
                // Read the EVENT's new value, not this.WindowState: the backend flaps Minimized->Normal so
                // fast that by handler time the property already reads Normal, and a current-state check
                // missed the transition every single time (proved by the 1009 trace: the flap printed,
                // "engaging" never did).
                if (e.Property != WindowStateProperty || e.NewValue is not WindowState.Minimized) return;
                if (_handlingMinimizeToTray) return;                    // tray drop owns this transition
                if (DataContext is MainWindowViewModel { Settings.MinimizeToTray: true }) return;   // ditto
                Dispatcher.UIThread.Post(() => { WindowState = WindowState.Normal; Hide(); });
            };
            // The yellow button is the ONE control still broken after builds 1009-1017: native minimize
            // bounces back (#13002) and every workaround lost to the backend (#12971/#18148). So remove
            // the capability itself - clear NSWindowStyleMaskMiniaturizable and the yellow light grays
            // out, which is honest UI and standard for plenty of mac apps. Close-to-tray covers the
            // "get it out of my way" need. One objc_msgSend round-trip, macOS-only, after the native
            // window exists.
            Opened += (_, _) =>
            {
                try
                {
                    if (TryGetPlatformHandle() is Avalonia.Platform.IMacOSTopLevelPlatformHandle mac
                        && mac.NSWindow != IntPtr.Zero)
                    {
                        const nuint miniaturizable = 1 << 2;   // NSWindowStyleMaskMiniaturizable
                        var mask = Grog.App.Services.MacInterop.GetStyleMask(mac.NSWindow);
                        Grog.App.Services.MacInterop.SetStyleMask(mac.NSWindow, mask & ~miniaturizable);
                    }
                }
                catch { /* best-effort: a failed mask tweak just leaves the yellow button live */ }
            };

            if (Avalonia.Application.Current?.TryGetFeature<Avalonia.Controls.ApplicationLifetimes.IActivatableLifetime>() is { } act)
                act.Activated += (_, e) =>
                {
                    if (e.Kind == Avalonia.Controls.ApplicationLifetimes.ActivationKind.Reopen)
                        Dispatcher.UIThread.Post(() => { Show(); Activate(); });
                };
        }

        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.OwnerWindowResolver = () => this;
                // Missed-notification escalation: a notification fired while the window is hidden or
                // minimized paints the tray orange (warning) or red (problem) until the user comes back.
                vm.TrayAttentionRequested += (level, body, fixNav) =>
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => EscalateTrayAttention(level, body, fixNav));
                // A quit from the tray that has to wait for the drive shows its line: bring the window back.
                vm.ClosingWaitShown += () => { if (!IsVisible) RestoreFromTray(); };
                // Collection view seeded Title-ascending so the header shows the starting sort; header
                // clicks keep updating this same view.
                var view = new Avalonia.Collections.DataGridCollectionView(vm.Library.Games);
                // (09-22, owner) Seeded from the saved sort, and every header click is saved: the grid comes back
                // the way it was left.
                var settings = vm.Session.Settings;
                var path = string.IsNullOrEmpty(settings.LibrarySortPath) ? nameof(ViewModels.GameRowViewModel.Title) : settings.LibrarySortPath;
                view.SortDescriptions.Add(Avalonia.Collections.DataGridSortDescription.FromPath(path,
                    settings.LibrarySortDescending ? System.ComponentModel.ListSortDirection.Descending : System.ComponentModel.ListSortDirection.Ascending));
                view.SortDescriptions.CollectionChanged += (_, _) =>
                {
                    var sd = view.SortDescriptions.FirstOrDefault();
                    if (sd?.PropertyPath is not { Length: > 0 } p) return;
                    settings.LibrarySortPath = p; settings.LibrarySortDescending = sd.Direction == System.ComponentModel.ListSortDirection.Descending;
                    settings.SaveSoon();
                };
                if (Find<DataGrid>("LibraryGrid") is { } libraryGrid) libraryGrid.ItemsSource = view;
                // Owner column: DataGrid columns are not in the visual tree, so IsVisible cannot bind --
                // mirror the VM flag here, on hookup and whenever accounts change it.
                vm.Library.PropertyChanged += (_, e) =>   // the Library VM owns the flag (S3.4); the root no longer re-raises names it does not declare
                {
                    if (e.PropertyName == nameof(LibraryViewModel.ShowOwnerColumn)) SyncOwnerColumn(vm);
                };
                SyncOwnerColumn(vm);
            }
        };

        // ---- Minimize / close to system tray (opt-in via Settings) ----
        SetupTray();
        // A deliberate quit pauses in-flight work and closes with no prompt; an X follows CloseAction --
        // quit, minimize to tray, or ask "Confirm Close or Minimize?".
        Closing += (_, e) =>
        {
            if (_forceClose) return;   // user already chose "Close & pause"; let it through
            if (DataContext is not MainWindowViewModel vm) return;
            // Staged extras placement (09-13): the leave guard covered page changes but not the window's X, so
            // a close silently discarded the staged choice. Same question, same prompt; answering it re-issues
            // the close (CloseAfterLeave), which then takes the normal quit / tray / confirm path below.
            if (!vm.Quitting && vm.Settings.ExtrasCard.HasPending)
            {
                e.Cancel = true;
                vm.Navigate("Settings");
                vm.Settings.ExtrasCard.AskBeforeLeaving(MainWindowViewModel.CloseTarget);
                return;
            }
            if (vm.Quitting)
            {
                if (vm.ShowClosingWait) { e.Cancel = true; return; }   // the quit is waiting for the drive; X again does not skip it
                vm.PauseAllForShutdown(); return;   // explicit quit: pause + close
            }
            if (vm.Settings.CloseAction == CloseWindowAction.Quit)
            {
                // Remembered "close means quit": same app-level shutdown as the dialog's Close and Dock
                // Quit, because letting THIS window-close proceed was a no-op on macOS. The shutdown
                // re-raises Closing with Quitting set, which sails through above - on every platform.
                e.Cancel = true;
                vm.PauseAllForShutdown();
                vm.RequestQuit();
                return;
            }
            // Gated on the tray actually being ON: hiding the window with no tray icon strands the app,
            // so fall through and ask instead.
            if (vm.Settings.CloseAction == CloseWindowAction.MinimizeToTray && vm.Settings.MinimizeToTray)
            {
                e.Cancel = true;
                DropToTray(vm);
                return;
            }
            e.Cancel = true;                                        // otherwise: confirm close vs minimize
            vm.RememberCloseChoice = false;
            vm.ShowCloseConfirm = true;
        };
        // Minimizing also drops to the tray (and resets to Normal so the next restore isn't minimized).
        PropertyChanged += (_, e) =>
        {
            // Re-entry guard: on X11 the WM reports WindowState changes back and the handler would
            // ping-pong with itself until the stack overflows. Costs Windows nothing.
            if (_handlingMinimizeToTray) return;
            // The EVENT's value, never this.WindowState: macOS flaps Minimized->Normal so fast that a
            // current-state read already sees Normal and the drop never fires (1010 trace). Windows/X11
            // never exposed the race, but the event value is correct everywhere.
            if (e.Property == WindowStateProperty && e.NewValue is WindowState.Minimized)
            {
                var vm0 = DataContext as MainWindowViewModel;
                if (vm0 is not null && vm0.Settings.MinimizeToTray && !vm0.Quitting) DropToTray(vm0);
            }
        };
        // Tray icon is only shown while the feature is on; follow the setting live.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not MainWindowViewModel vm) return;
            vm.CloseAfterLeave += () => Close();   // the leave prompt answered a window close: close again, now unstaged
            if (_tray is not null) _tray.IsVisible = vm.Settings.MinimizeToTray;
            vm.Settings.PropertyChanged += (_, pe) =>   // the Settings VM owns the flag (S3.4)
            {
                if (pe.PropertyName == nameof(SettingsViewModel.MinimizeToTray) && _tray is not null)
                    _tray.IsVisible = vm.Settings.MinimizeToTray;
            };
        };

        var grid = Find<DataGrid>("LibraryGrid");
        if (grid is not null)
        {
            grid.SelectionChanged += (_, _) =>
            {
                if (DataContext is MainWindowViewModel vm)
                    vm.Library.UpdateSelection(grid.SelectedItems);
            };
            DataContextChanged += (_, _) =>
            {
                if (DataContext is MainWindowViewModel vm)
                    vm.Library.SelectionClearRequested += () => grid.SelectedItems.Clear();
            };
            // Esc backs out one step: first a temporary filter detour, otherwise the selection.
            KeyDown += (_, e) =>
            {
                if (e.Key != Avalonia.Input.Key.Escape) return;

                // The Backups inventory has its own selection; Esc should clear it first.
                if (DataContext is MainWindowViewModel ivm && ivm.Storage.ClearInventorySelectionIfAny())
                {
                    e.Handled = true;
                    return;
                }
                if (DataContext is MainWindowViewModel vm && vm.TryEndDetour()) { e.Handled = true; return; }
                if (grid.SelectedItems.Count > 0)
                {
                    grid.SelectedItems.Clear();
                    e.Handled = true;
                }
            };
        }

        WireRowReordering<QueueRow>("WaitingList", vm => vm.Overview.DownloadTail,
            // The drawn tail groups fitting rows above the won't-fit divider (09-16), so a drop index is mapped
            // back onto the persisted queue; the pinned pill holds position 0 either way.
            (vm, row, i) => { int p = vm.Overview.PersistedIndexOfTailDrop(i, row); if (p >= 0) vm.Overview.MoveQueueRow(row, p); },
            // A fitting row cannot be dropped into the won't-fit group: fit is decided by the planner, not by
            // where the pointer lets go, so the drop would only pop the row back up (owner-hit 09-16). The
            // divider itself is a valid target (bottom of the white group); red rows may go anywhere.
            (vm, row, target) => vm.Overview.CanDropOn(row, target));
        WireRowReordering<ReorgMoveRow>("MoveWaitingList", vm => vm.Storage.ReorgTail,
            // The tail excludes the pinned head, so shift by 1 into the pending list when nothing is active.
            (vm, row, i) => vm.Storage.MoveReorgRow(row, i + (vm.Storage.HasReorgActive ? 0 : 1)));
        WireInventoryDrag();
        WirePillResponsiveness();
        WireChromeWidth();   // (09-09) queue sort pill labels shorten before they can wrap

        // The Library page may not be realized yet (Overview is home), so bind on every layout pass until
        // it is; TryBindPillControls short-circuits on a flag once bound.
        LayoutUpdated += (_, _) => { if (TryBindPillControls()) RecomputePillMode(); };

        // Column ActualWidth is only meaningful once the grid has laid out, so restore on Opened.
        Opened += (_, _) => RestoreColumnWidths(grid);
        // One-shot trim after ~8s: hands the transient startup pages (JIT, first-scan buffers) back to the
        // OS so the foreground footprint drops to steady-state without waiting for a minimize.
        Opened += (_, _) => TrimWorkingSetSoon(8000);
    }

}
