using System.Collections.Concurrent;
using System.Net;

namespace WebLimiter.Core.Engine;

internal readonly record struct ConnKey(byte Proto, ushort LocalPort, UInt128 Remote, ushort RemotePort, bool V6)
{
    public IPAddress RemoteAddress()
    {
        if (!V6) return new IPAddress(new[] { (byte)(Remote >> 24), (byte)(Remote >> 16), (byte)(Remote >> 8), (byte)Remote });
        var bytes = new byte[16];
        for (int i = 0; i < 16; i++) bytes[i] = (byte)(Remote >> (8 * (15 - i)));
        return new IPAddress(bytes);
    }
}

internal sealed class Counter
{
    public long Down;            // cumulative bytes, written with Interlocked by the packet thread
    public long Up;
    public long LastDown;        // values at the previous tick (timer thread only)
    public long LastUp;
    public double DownRate;
    public double UpRate;
    public long LastSeen = Environment.TickCount64;

    public void Add(bool outbound, int bytes)
    {
        if (outbound) Interlocked.Add(ref Up, bytes);
        else Interlocked.Add(ref Down, bytes);
        LastSeen = Environment.TickCount64;
    }

    /// <summary>Computes rates since the last tick and returns the byte deltas.</summary>
    public (long Down, long Up) Tick(double seconds)
    {
        long d = Interlocked.Read(ref Down), u = Interlocked.Read(ref Up);
        long dd = d - LastDown, du = u - LastUp;
        LastDown = d;
        LastUp = u;
        DownRate = dd / seconds;
        UpRate = du / seconds;
        return (dd, du);
    }
}

/// <summary>Live traffic state of one application (all of its processes).</summary>
internal sealed class AppTraffic
{
    public AppTraffic(string path, string name, string[] services, string? exePath)
    {
        Path = path;
        Name = name;
        Services = services;
        ExePath = exePath;
    }

    public string Path { get; }
    public string Name { get; }
    /// <summary>Windows services hosted by this app's process (svchost), for service rules.</summary>
    public string[] Services { get; }
    /// <summary>Executable of a service host (svchost.exe), whose own rule also applies.</summary>
    public string? ExePath { get; }
    public readonly Counter Counter = new();
    public readonly Shaper DownShaper = new();
    public readonly Shaper UpShaper = new();
    public readonly ConcurrentDictionary<ConnKey, Counter> Connections = new();

    public volatile bool Blocked;
    public volatile bool QuotaExceeded;
}
