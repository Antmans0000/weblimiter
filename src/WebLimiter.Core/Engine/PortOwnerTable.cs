using System.Collections.Concurrent;
using System.Diagnostics;
using WebLimiter.Core.Native;

namespace WebLimiter.Core.Engine;

/// <summary>
/// Maps (protocol, local port) to the owning process id. Fed live by WinDivert SOCKET-layer
/// events and backed by the IP Helper tables for sockets that existed before startup.
/// </summary>
internal sealed class PortOwnerTable
{
    private static readonly long RefreshInterval = Stopwatch.Frequency / 4; // 250 ms

    private readonly ConcurrentDictionary<int, int> _owners = new();
    private long _lastRefresh;
    private readonly object _refreshLock = new();

    private static int Key(byte proto, ushort port) => (proto << 16) | port;

    private const int SystemPid = 4;

    /// <summary>
    /// Records a socket-layer owner. Kernel components that proxy or inspect traffic (antivirus
    /// web shields, VPN filters) re-emit CONNECT events as the System process right after the
    /// real application; those must not steal a port still held by a live user process.
    /// </summary>
    public void Set(byte proto, ushort port, int pid)
    {
        int key = Key(proto, port);
        if (pid == SystemPid && _owners.TryGetValue(key, out int current) && current > SystemPid
            && Win32.IsProcessAlive(current))
            return;
        _owners[key] = pid;
    }

    public int Lookup(byte proto, ushort port)
    {
        int key = Key(proto, port);
        if (_owners.TryGetValue(key, out int pid)) return pid;
        if (Stopwatch.GetTimestamp() - Interlocked.Read(ref _lastRefresh) < RefreshInterval) return -1;
        Refresh();
        return _owners.TryGetValue(key, out pid) ? pid : -1;
    }

    public void Refresh()
    {
        lock (_refreshLock)
        {
            Interlocked.Exchange(ref _lastRefresh, Stopwatch.GetTimestamp());
            try
            {
                foreach (var (proto, port, pid) in Win32.EnumeratePortOwners())
                    Set(proto, port, pid);
            }
            catch (Exception ex) { Storage.DataPaths.LogError("Table des ports illisible", ex); }
        }
    }
}
