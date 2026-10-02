// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Grog.App.ViewModels;
using Grog.App.Views;

namespace Grog.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    private Views.MainWindow? _mainWindow;

    /// <summary>The app menu's "About Grog" (macOS): surface the window and open the in-app About page.</summary>
    private void OnAboutMenuClick(object? sender, System.EventArgs e)
    {
        if (_mainWindow is null) return;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _mainWindow.BringToForeground();
            (_mainWindow.DataContext as ViewModels.MainWindowViewModel)?.NavigateCommand.Execute("About");
        });
    }

    private Grog.Core.Storage.ProcessPresence? _presence;   // held for the process lifetime
    private Grog.Core.Storage.LogFile? _milestones;
    private void Milestone(string what)
    {
        try { (_milestones ??= new Grog.Core.Storage.LogFile(Grog.Core.Storage.GrogPaths.ResolveConfigDir())).Append(System.DateTimeOffset.Now, "app", false, $"startup: {what}"); }
        catch { }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Wire keyring diagnostics into grog.log BEFORE anything can touch a token, so every
        // secret-tool spawn is attributable to a call path in the log.
        try
        {
            var klog = new Grog.Core.Storage.LogFile(Grog.Core.Storage.GrogPaths.Resolve().ConfigDir);
            // Crash guard FIRST: from here on no unhandled exception leaves the process without a stack
            // in grog.log (the toast is wired once the view-model exists, below).
            Services.CrashGuard.Install(klog);
            Grog.Core.Platform.OsKeyring.Trace = s => klog.Append(System.DateTimeOffset.Now, "keyring", false, s);
            // Startup line pins WHICH build wrote the entries that follow: a stale still-running instance
            // is indistinguishable from the fresh one in the log without it, and that ambiguity burned a
            // GOG login on the 2026-08-23 walk. Exe mtime = the build, PID = the instance.
            var exe = System.Environment.ProcessPath;
            var built = exe is null ? "?" : System.IO.File.GetLastWriteTime(exe).ToString("yyyy-MM-dd HH:mm:ss");
            klog.Append(System.DateTimeOffset.Now, "app", false,
                $"startup: pid {System.Environment.ProcessId}, binary built {built}, {exe}");
            // The App owns the profile while it runs (09-08): a CLI writer verb checks this lock and refuses.
            _presence = Grog.Core.Storage.ProcessPresence.TryHold(
                Grog.Core.Storage.GrogPaths.ResolveConfigDir(), Grog.Core.Storage.ProcessPresence.AppLockName, "app");
        }
        catch { /* diagnostics only */ }

        // Apply the saved accent theme before the first window paints, so it opens in the right colors.
        // (UI-thread sweep 09-06 r2) Resolve() / File.GetLastWriteTime / AppSettings.Load above and here are
        // synchronous disk reads on the dispatcher, kept deliberately: they run ONCE before any window exists.
        var settings = AppSettings.Load();
        ThemeService.Apply(settings.Theme);
        // Bring up the desktop-notification backend (best-effort, async -- never blocks startup).
        Notifier.Enabled = settings.DesktopNotifications;
        _ = Notifier.InitializeAsync();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Startup milestones in grog.log (owner-hit 09-08: one launch sat with a taskbar icon and no window;
            // nothing after "startup:" said where it stopped). Each line is one append on the diagnostics log.
            var vm = new MainWindowViewModel();
            Milestone("view model built");
            var window = new MainWindow { DataContext = vm };
            Milestone("window created");
            // Dev knob: GROG_WINDOW_SIZE=1366x768 overrides the startup size for live layout checks
            // at the floor resolution; unset means the axaml's size.
            if (System.Environment.GetEnvironmentVariable("GROG_WINDOW_SIZE") is { Length: > 2 } ws)
            {
                var parts = ws.Split('x', 'X');
                if (parts.Length == 2 && double.TryParse(parts[0], out var w) && double.TryParse(parts[1], out var h))
                { window.Width = w; window.Height = h; }
            }
            _mainWindow = window;
            desktop.MainWindow = window;
            window.Opened += (_, _) => Milestone("window opened");
            if (window.DataContext is MainWindowViewModel crashVm)
                Services.CrashGuard.SetNotifier(msg => crashVm.ReportUnhandled(msg));
            // An OS-level quit (macOS Cmd+Q / Dock -> Quit, session logout anywhere) means QUIT, not
            // "ask close-vs-tray": without this the Closing interceptor cancels the shutdown and the app
            // looks unkillable from the Dock (MacinCloud 2026-08-28). PauseAllForShutdown still runs via
            // the normal Quitting path, so in-flight work is parked exactly as with Exit Grog.
            desktop.ShutdownRequested += (_, e) =>
            {
                if (window.DataContext is not MainWindowViewModel vm || vm.Quitting) return;
                // Files still open: hold the OS quit and take the waiting path, so the writes in flight land first.
                if (vm.HasWritesInFlight) { e.Cancel = true; vm.RequestQuit(); return; }
                vm.NoteOsQuit();
            };
            // A second launch signals us (on a background thread) to come forward -- marshal to the UI thread
            // and bring the window out of the tray.
            SingleInstance.ShowRequested = () =>
                Avalonia.Threading.Dispatcher.UIThread.Post(window.BringToForeground);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
