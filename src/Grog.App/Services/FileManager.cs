// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System.Runtime.InteropServices;

namespace Grog.App.Services;

public static class FileManager
{
    public static void Open(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            var (cmd, args) =
                RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ("explorer.exe", $"\"{path}\"")
              : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? ("open", $"\"{path}\"")
              : ("xdg-open", $"\"{path}\"");
            // Off the dispatcher: ShellExecute on a slow or network path can hold the caller.
            System.Threading.Tasks.Task.Run(() =>
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(cmd, args) { UseShellExecute = true }); }
                catch { /* best-effort */ }
            });
        }
        catch { /* best-effort: no file manager, or path not on disk yet */ }
    }
}
