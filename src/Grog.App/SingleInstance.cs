// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;

namespace Grog.App;

public static class SingleInstance
{
    private const string MutexName = "Grog.SingleInstance.9E1C0F2A";
    private const string ShowEventName = "Grog.ShowRequest.9E1C0F2A";

    private static Mutex? _mutex;
    private static EventWaitHandle? _showEvent;
    private static FileStream? _lockFile;
    private static Socket? _listenSocket;

    public static Action? ShowRequested;

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

    public static bool TryAcquire()
    {
        if (OperatingSystem.IsWindows()) return TryAcquireWindows();
        try
        {
            Directory.CreateDirectory(RuntimeDir);
            _lockFile = new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

            // Primary
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
            return false;
        }
        catch
        {
            return true;
        }
    }

    private static bool TryAcquireWindows()
    {
        try
        {
            _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
            if (!createdNew) return false;

            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            var listener = new Thread(ListenForShowRequests) { IsBackground = true, Name = "Grog-ShowListener" };
            listener.Start();
            return true;
        }
        catch
        {
            return true;   // fail OPEN
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
