// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Grog.App.Services;

// Linux only. The sign-in dialog needs GTK, which fails to start under some Wayland compositors, and GTK
// gets one start per process. So a helper copy of Grog probes it first; if the default fails, X11 is
// probed, and a working X11 is applied to this process and remembered in the config dir.
public static class GtkBackendGuard
{
    public const string ProbeArg = "--probe-gtk";
    public const string X11 = "x11";
    private const string MarkerName = "gtk-backend";
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);
    private static Task<bool>? _ready;

    // Helper-process entry point: 0 when GTK starts, 1 when it does not.
    public static int RunProbe()
    {
        try { return gtk_init_check(IntPtr.Zero, IntPtr.Zero) ? 0 : 1; }
        catch (Exception) { return 1; }   // no GTK library at all is a failed probe, nothing more
    }

    // True when the sign-in dialog can start. Decided once per process, on a pool thread.
    public static Task<bool> EnsureAsync()
        => OperatingSystem.IsLinux() ? (_ready ??= Task.Run(DecideForThisProcess)) : Task.FromResult(true);

    private static bool DecideForThisProcess()
    {
        try
        {
            return Decide(Environment.GetEnvironmentVariable("GDK_BACKEND"),
                          Path.Combine(Grog.Core.Storage.GrogPaths.Resolve().ConfigDir, MarkerName),
                          Probe, Apply);
        }
        catch (Exception ex) { Trace.WriteLine($"Grog: GTK backend check failed: {ex.Message}"); return false; }
    }

    // The decision, with its side effects injected so it can be tested anywhere.
    public static bool Decide(string? userBackend, string markerPath, Func<string?, bool> probe, Action<string> apply)
    {
        if (!string.IsNullOrEmpty(userBackend)) return true;   // the user's own setting always wins
        if (ReadMarker(markerPath) == X11)
        {
            Trace.WriteLine($"Grog: using the remembered X11 display for sign-in (delete {markerPath} to re-test).");
            apply(X11);
            return true;
        }
        if (probe(null)) return true;
        if (!probe(X11)) return false;
        apply(X11);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
            File.WriteAllText(markerPath, X11 + "\n");
        }
        catch (Exception ex) { Trace.WriteLine($"Grog: could not remember the X11 choice: {ex.Message}"); }
        return true;
    }

    private static string? ReadMarker(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
        catch (Exception ex) { Trace.WriteLine($"Grog: could not read {path}: {ex.Message}"); return null; }
    }

    private static bool Probe(string? backend)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return false;
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            psi.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);   // run via `dotnet Grog.dll`
        psi.ArgumentList.Add(ProbeArg);
        if (backend is null) psi.Environment.Remove("GDK_BACKEND");
        else psi.Environment["GDK_BACKEND"] = backend;
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return false;
            if (!p.WaitForExit(ProbeTimeout)) { try { p.Kill(entireProcessTree: true); } catch (Exception) { /* already exited */ } return false; }
            return p.ExitCode == 0;
        }
        catch (Exception ex) { Trace.WriteLine($"Grog: GTK probe could not run: {ex.Message}"); return false; }
    }

    // GTK reads the native environment, which Environment.SetEnvironmentVariable does not reach on Unix.
    private static void Apply(string backend)
    {
        setenv("GDK_BACKEND", backend, 1);
        Environment.SetEnvironmentVariable("GDK_BACKEND", backend);
    }

    [DllImport("libgtk-3.so.0")]
    private static extern bool gtk_init_check(IntPtr argc, IntPtr argv);

    [DllImport("libc.so.6", SetLastError = true)]
    private static extern int setenv(string name, string value, int overwrite);
}
