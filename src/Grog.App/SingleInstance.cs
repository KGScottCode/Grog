// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;

namespace Grog.App;

/// <summary>Keeps Grog to ONE running instance per user; a second launch signals the first to come forward
/// and then exits. Two instances would mean concurrent manifest writers -- a data-loss risk.</summary>
/// <remarks>Windows: named Mutex + auto-reset EventWaitHandle (session-scoped). Linux/macOS: an exclusively
/// locked lock file plus a Unix domain socket the second launch connects to as the show signal.</remarks>
public static class SingleInstance
{
    // Session-scoped names (per interactive login), so two different users can each run their own Grog.
    private const string MutexName = "Grog.SingleInstance.9E1C0F2A";
    private const string ShowEventName = "Grog.ShowRequest.9E1C0F2A";

    private static Mutex? _mutex;
    private static EventWaitHandle? _showEvent;
    private static FileStream? _lockFile;
    private static Socket? _listenSocket;

    /// <summary>Invoked (on a background thread) when a second launch asks the running instance to show itself.
    /// The app wires this to surface the main window. Marshal to the UI thread inside the handler.</summary>
    public static Action? ShowRequested;

    // $XDG_RUNTIME_DIR is per-user tmpfs, cleared at logout -- the right home for a lock and a socket.
    // Fall back to the config dir when absent; stale files are handled by the lock probe, not existence.
    private static string RuntimeDir
    {
        get
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            if (!string.IsNullOrEmpty(xdg) && Directory.Exists(xdg)) return xdg;
            return Grog.Core.Storage.GrogPaths.Resolve().ConfigDir;
        }
    }
    private static string LockPath => Path.Combine(RuntimeDir, "grog-instance.lock");
    private static string SocketPath => Path.Combine(RuntimeDir, "grog-show.sock");

    /// <summary>True if THIS process is the primary instance and should start normally. False means another Grog
    /// is already running (the caller should <see cref="SignalExisting"/> and exit).</summary>
    public static bool TryAcquire()
    {
        if (OperatingSystem.IsWindows()) return TryAcquireWindows();
        try
        {
            Directory.CreateDirectory(RuntimeDir);
            // FileShare.None takes an exclusive advisory lock on Unix; a second process gets IOException.
            // The stream is held for the process lifetime -- the LOCK is the truth, file existence proves nothing.
            _lockFile = new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

            // Primary: listen on the domain socket so a second launch can ask us to surface.
            try { File.Delete(SocketPath); } catch { /* stale from a crash; bind below recreates it */ }
            _listenSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            _listenSocket.Bind(new UnixDomainSocketEndPoint(SocketPath));
            _listenSocket.Listen(1);
            var listener = new Thread(ListenForShowConnects) { IsBackground = true, Name = "Grog-ShowListener" };
            listener.Start();
            return true;
        }
        catch (IOException)
        {
            return false;   // lock held: another Grog is running
        }
        catch
        {
            // If the OS refuses the primitives for any reason, fail OPEN (run normally) rather than block launch.
            return true;
        }
    }

    private static bool TryAcquireWindows()
    {
        try
        {
            _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
            if (!createdNew) return false;

            // We're the primary: open a listener the secondary can poke to ask us to surface.
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            var listener = new Thread(ListenForShowRequests) { IsBackground = true, Name = "Grog-ShowListener" };
            listener.Start();
            return true;
        }
        catch
        {
            return true;   // fail OPEN, as above
        }
    }

    private static void ListenForShowRequests()
    {
        var ev = _showEvent;
        if (ev is null) return;
        while (true)
        {
            try { ev.WaitOne(); } catch { return; }
            try { ShowRequested?.Invoke(); } catch { /* handler marshals + best-effort */ }
        }
    }

    private static void ListenForShowConnects()
    {
        var s = _listenSocket;
        if (s is null) return;
        while (true)
        {
            Socket client;
            try { client = s.Accept(); } catch { return; }   // socket disposed on shutdown
            try { ShowRequested?.Invoke(); } catch { /* handler marshals + best-effort */ }
            try { client.Dispose(); } catch { }
        }
    }

    /// <summary>Called by a SECOND launch: wake the running instance so it comes to the foreground.</summary>
    public static void SignalExisting()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(ShowEventName, out var ev))
                {
                    ev.Set();
                    ev.Dispose();
                }
            }
            catch { /* best-effort: worst case the second launch just exits without surfacing the first */ }
            return;
        }
        try
        {
            using var c = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            c.Connect(new UnixDomainSocketEndPoint(SocketPath));   // the connect IS the signal
        }
        catch { /* best-effort, as above */ }
    }

    /// <summary>Release the instance lock on shutdown.</summary>
    public static void Release()
    {
        try { _mutex?.ReleaseMutex(); } catch { /* not owned / already gone */ }
        _mutex?.Dispose();
        _showEvent?.Dispose();
        try { _listenSocket?.Dispose(); } catch { }
        try { _lockFile?.Dispose(); } catch { }
        try { if (_lockFile is not null) File.Delete(LockPath); } catch { }
        try { if (_listenSocket is not null) File.Delete(SocketPath); } catch { }
    }
}
