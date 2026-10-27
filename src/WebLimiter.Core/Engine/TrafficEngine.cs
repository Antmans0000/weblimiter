using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using WebLimiter.Core.Model;
using WebLimiter.Core.Native;
using WebLimiter.Core.Storage;

namespace WebLimiter.Core.Engine;

public sealed class WinDivertException : Exception
{
    public WinDivertException(int code) : base(Describe(code)) => Code = code;

    public int Code { get; }

    private static string Describe(int code) => code switch
    {
        2 => "WinDivert64.sys est introuvable à côté de WinDivert.dll.",
        5 => "Accès refusé : WebLimiter doit être lancé en tant qu'administrateur.",
        87 => "Filtre WinDivert invalide.",
        126 => "WinDivert.dll est introuvable.",
        577 => "La signature du pilote WinDivert n'est pas reconnue par Windows.",
        654 => "Une autre version du pilote WinDivert est déjà chargée (redémarrez Windows).",
        1060 or 1275 => "Le chargement du pilote WinDivert a été bloqué (antivirus ou sécurité Windows).",
        1753 => "Le service « Moteur de filtrage de base » (BFE) est arrêté.",
        _ => $"Impossible d'ouvrir WinDivert (erreur Windows {code}).",
    };
}

/// <summary>
/// Diverts every non-loopback IP packet through user mode, attributes it to a process,
/// accounts it, and then drops it (block), delays it (rate limit) or reinjects it.
/// </summary>
public sealed unsafe class TrafficEngine : IDisposable
{
    private const int PacketBufferSize = 4 * 1024 * 1024;
    private const int AddrSize = 80;
    private const long ConnectionIdleMs = 30_000;

    private readonly PortOwnerTable _ports = new();
    private readonly ProcessResolver _processes = new();
    private readonly ConcurrentDictionary<string, AppTraffic> _apps = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, AppTraffic> _byPid = new();
    private AppTraffic[] _appList = Array.Empty<AppTraffic>();

    private IntPtr _net = WinDivertNative.InvalidHandle;
    private IntPtr _sock = WinDivertNative.InvalidHandle;
    private Thread? _netThread, _sockThread, _schedThread;
    private Timer? _timer;
    private readonly Stopwatch _tickClock = new();
    private long _tickCount;
    private volatile bool _running;
    private volatile EngineSnapshot _snapshot = EngineSnapshot.Empty;

    public TrafficEngine(RuleStore rules, StatsStore stats)
    {
        Rules = rules;
        Stats = stats;
    }

    public RuleStore Rules { get; }
    public StatsStore Stats { get; }
    public bool IsRunning => _running;
    public EngineSnapshot Snapshot => _snapshot;

    /// <summary>Optional diagnostic sink (socket events, attribution decisions).</summary>
    public Action<string>? Trace { get; set; }

    /// <summary>Raised once per second on a thread-pool thread.</summary>
    public event Action<EngineSnapshot>? Updated;

    public void Start()
    {
        if (_running) return;
        _ports.Refresh();

        _net = Open("!loopback", WinDivertLayer.Network, 0, WinDivertFlags.None);
        WinDivertNative.WinDivertSetParam(_net, WinDivertParam.QueueLength, 16384);
        WinDivertNative.WinDivertSetParam(_net, WinDivertParam.QueueTime, 2000);
        WinDivertNative.WinDivertSetParam(_net, WinDivertParam.QueueSize, 32 * 1024 * 1024);

        try
        {
            _sock = Open("!loopback and (tcp or udp)", WinDivertLayer.Socket, 1,
                WinDivertFlags.Sniff | WinDivertFlags.RecvOnly);
        }
        catch (WinDivertException ex)
        {
            // Not fatal: attribution falls back to the IP Helper tables.
            DataPaths.LogError("Couche SOCKET indisponible", ex);
        }

        _running = true;
        Win32.timeBeginPeriod(1);
        Rules.Changed += ApplyAllRules;

        _netThread = StartThread(NetworkLoop, "WebLimiter.Network", ThreadPriority.AboveNormal);
        _schedThread = StartThread(SchedulerLoop, "WebLimiter.Scheduler", ThreadPriority.AboveNormal);
        if (_sock != WinDivertNative.InvalidHandle)
            _sockThread = StartThread(SocketLoop, "WebLimiter.Socket", ThreadPriority.Normal);

        _tickClock.Restart();
        _timer = new Timer(_ => Tick(), null, 1000, 1000);
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        Rules.Changed -= ApplyAllRules;
        _timer?.Dispose();
        _timer = null;

        foreach (var h in new[] { _net, _sock })
            if (h != WinDivertNative.InvalidHandle) WinDivertNative.WinDivertShutdown(h, 3);
        _netThread?.Join(2000);
        _sockThread?.Join(2000);
        _schedThread?.Join(2000);
        foreach (var h in new[] { _net, _sock })
            if (h != WinDivertNative.InvalidHandle) WinDivertNative.WinDivertClose(h);
        _net = _sock = WinDivertNative.InvalidHandle;

        foreach (var app in _appList)
        {
            app.DownShaper.Clear();
            app.UpShaper.Clear();
        }
        Win32.timeEndPeriod(1);
        Stats.Save();
        _snapshot = EngineSnapshot.Empty;
    }

    public void Dispose() => Stop();

    public IReadOnlyList<ConnectionSnapshot> GetConnections(string appPath)
    {
        if (!_apps.TryGetValue(appPath, out var app)) return Array.Empty<ConnectionSnapshot>();
        return app.Connections
            .Select(kv => new ConnectionSnapshot(
                kv.Key.Proto switch { 6 => "TCP", 17 => "UDP", 1 => "ICMP", 58 => "ICMPv6", var p => $"IP/{p}" },
                kv.Key.LocalPort, kv.Key.RemoteAddress().ToString(), kv.Key.RemotePort,
                kv.Value.DownRate, kv.Value.UpRate,
                Interlocked.Read(ref kv.Value.Down), Interlocked.Read(ref kv.Value.Up)))
            .OrderByDescending(c => c.DownRate + c.UpRate)
            .ThenByDescending(c => c.TotalDown + c.TotalUp)
            .ToList();
    }

    private static IntPtr Open(string filter, WinDivertLayer layer, short priority, WinDivertFlags flags)
    {
        IntPtr h;
        try { h = WinDivertNative.WinDivertOpen(filter, layer, priority, flags); }
        catch (DllNotFoundException) { throw new WinDivertException(126); }
        if (h == WinDivertNative.InvalidHandle) throw new WinDivertException(Marshal.GetLastWin32Error());
        return h;
    }

    private static Thread StartThread(ThreadStart body, string name, ThreadPriority priority)
    {
        var t = new Thread(body) { IsBackground = true, Name = name, Priority = priority };
        t.Start();
        return t;
    }

    // ---- packet path ------------------------------------------------------------------

    private void NetworkLoop()
    {
        byte* buffer = (byte*)NativeMemory.Alloc(PacketBufferSize);
        var addrs = (WinDivertAddress*)NativeMemory.Alloc(WinDivertNative.BatchMax * AddrSize);
        try
        {
            while (_running)
            {
                uint addrLen = WinDivertNative.BatchMax * AddrSize;
                if (!WinDivertNative.WinDivertRecvEx(_net, buffer, PacketBufferSize, out uint recvLen, 0,
                        addrs, ref addrLen, IntPtr.Zero))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (!_running || err is WinDivertNative.ErrorNoData or WinDivertNative.ErrorInvalidHandle
                            or WinDivertNative.ErrorOperationAborted) break;
                    continue;
                }

                int count = (int)(addrLen / AddrSize);
                byte* p = buffer;
                uint remaining = recvLen;
                for (int i = 0; i < count && remaining > 0; i++)
                {
                    try { remaining = HandlePacket(ref p, remaining, ref addrs[i]); }
                    catch (Exception ex)
                    {
                        DataPaths.LogError("Erreur de traitement de paquet", ex);
                        break;
                    }
                }
            }
        }
        finally
        {
            NativeMemory.Free(buffer);
            NativeMemory.Free(addrs);
        }
    }

    /// <summary>Handles the packet at <paramref name="p"/>, advances p and returns the bytes left in the batch.</summary>
    private uint HandlePacket(ref byte* p, uint remaining, ref WinDivertAddress addr)
    {
        byte* ip4 = null, ip6 = null, tcp = null, udp = null, next = null;
        byte proto = 0;
        uint nextLen = 0;
        WinDivertNative.WinDivertHelperParsePacket(p, remaining, &ip4, &ip6, &proto, null, null,
            &tcp, &udp, null, null, &next, &nextLen);

        int length = next != null && next > p ? (int)(next - p) : IpLength(p, remaining);
        byte* packet = p;
        p += length;
        uint left = remaining - (uint)length;

        bool outbound = addr.Outbound;
        UInt128 remote = 0;
        bool v6 = false;
        if (ip4 != null)
        {
            remote = BinaryPrimitives.ReadUInt32BigEndian(new ReadOnlySpan<byte>(outbound ? ip4 + 16 : ip4 + 12, 4));
        }
        else if (ip6 != null)
        {
            v6 = true;
            var span = new ReadOnlySpan<byte>(outbound ? ip6 + 24 : ip6 + 8, 16);
            remote = BinaryPrimitives.ReadUInt128BigEndian(span);
        }

        byte* l4 = tcp != null ? tcp : udp;
        ushort localPort = 0, remotePort = 0;
        AppTraffic app;
        if (l4 != null)
        {
            ushort src = BinaryPrimitives.ReadUInt16BigEndian(new ReadOnlySpan<byte>(l4, 2));
            ushort dst = BinaryPrimitives.ReadUInt16BigEndian(new ReadOnlySpan<byte>(l4 + 2, 2));
            localPort = outbound ? src : dst;
            remotePort = outbound ? dst : src;
            proto = tcp != null ? (byte)6 : (byte)17;
            app = AppFor(_ports.Lookup(proto, localPort));
        }
        else
        {
            app = AppFor(4); // ICMP and other protocols belong to the system
        }

        var connection = app.Connections.GetOrAdd(new ConnKey(proto, localPort, remote, remotePort, v6),
            static _ => new Counter());
        connection.LastSeen = Environment.TickCount64;

        if (app.Blocked) return left; // drop

        var shaper = outbound ? app.UpShaper : app.DownShaper;
        if (shaper.Admit(packet, length, addr, app.Counter, connection) == Verdict.Pass)
        {
            fixed (WinDivertAddress* a = &addr)
                WinDivertNative.WinDivertSend(_net, packet, (uint)length, out _, a);
            app.Counter.Add(outbound, length);
            connection.Add(outbound, length);
        }
        return left;
    }

    private static int IpLength(byte* p, uint remaining)
    {
        int len = 0;
        if (remaining >= 20 && p[0] >> 4 == 4) len = (p[2] << 8) | p[3];
        else if (remaining >= 40 && p[0] >> 4 == 6) len = 40 + ((p[4] << 8) | p[5]);
        return len <= 0 || len > remaining ? (int)remaining : len;
    }

    private AppTraffic AppFor(int pid)
    {
        if (_byPid.TryGetValue(pid, out var app)) return app;
        var id = _processes.Resolve(pid);
        app = _apps.GetOrAdd(id.Path, path =>
        {
            var created = new AppTraffic(path, id.Name, id.Services, id.ExePath);
            ApplyRule(created, DateTime.Now);
            return created;
        });
        lock (_apps)
        {
            if (!_appList.Contains(app)) _appList = _appList.Append(app).ToArray();
        }
        _byPid[pid] = app;
        return app;
    }

    private void SchedulerLoop()
    {
        var ready = new List<HeldPacket>(512);
        while (_running)
        {
            foreach (var app in _appList)
            {
                if (app.DownShaper.HasQueue) app.DownShaper.Release(ready, app.Blocked);
                if (app.UpShaper.HasQueue) app.UpShaper.Release(ready, app.Blocked);
            }
            foreach (var held in ready)
            {
                var addr = held.Address;
                fixed (byte* b = held.Buffer)
                    WinDivertNative.WinDivertSend(_net, b, (uint)held.Length, out _, &addr);
                held.App.Add(addr.Outbound, held.Length);
                held.Connection.Add(addr.Outbound, held.Length);
                ArrayPool<byte>.Shared.Return(held.Buffer);
            }
            ready.Clear();
            Thread.Sleep(1);
        }
    }

    // ---- socket events (process attribution) ------------------------------------------

    private void SocketLoop()
    {
        var addrs = (WinDivertAddress*)NativeMemory.Alloc(WinDivertNative.BatchMax * AddrSize);
        try
        {
            while (_running)
            {
                uint addrLen = WinDivertNative.BatchMax * AddrSize;
                if (!WinDivertNative.WinDivertRecvEx(_sock, null, 0, out _, 0, addrs, ref addrLen, IntPtr.Zero))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (!_running || err is WinDivertNative.ErrorNoData or WinDivertNative.ErrorInvalidHandle
                            or WinDivertNative.ErrorOperationAborted) break;
                    continue;
                }
                int count = (int)(addrLen / AddrSize);
                for (int i = 0; i < count; i++)
                {
                    ref var a = ref addrs[i];
                    Trace?.Invoke($"sock {a.Event} proto={a.Protocol} lport={a.LocalPort} rport={a.RemotePort} pid={a.ProcessId}");
                    if (a.Event is WinDivertEvent.SocketBind or WinDivertEvent.SocketConnect
                            or WinDivertEvent.SocketListen or WinDivertEvent.SocketAccept
                        && a.LocalPort != 0 && a.Protocol is 6 or 17)
                    {
                        _ports.Set(a.Protocol, a.LocalPort, (int)a.ProcessId);
                    }
                }
            }
        }
        finally { NativeMemory.Free(addrs); }
    }

    // ---- once per second --------------------------------------------------------------

    private void Tick()
    {
        try
        {
            double seconds = Math.Max(0.001, _tickClock.Elapsed.TotalSeconds);
            _tickClock.Restart();
            var now = DateTime.Now;
            long nowMs = Environment.TickCount64;
            long tick = ++_tickCount;

            var apps = new List<AppSnapshot>(_appList.Length);
            double totalDown = 0, totalUp = 0;
            foreach (var app in _appList)
            {
                var (dd, du) = app.Counter.Tick(seconds);
                Stats.Add(app.Path, dd, du, now);

                foreach (var (key, conn) in app.Connections)
                {
                    conn.Tick(seconds);
                    if (nowMs - conn.LastSeen > ConnectionIdleMs) app.Connections.TryRemove(key, out _);
                }

                ApplyRule(app, now);

                totalDown += app.Counter.DownRate;
                totalUp += app.Counter.UpRate;
                apps.Add(new AppSnapshot(app.Path, app.Name, app.Counter.DownRate, app.Counter.UpRate,
                    Interlocked.Read(ref app.Counter.Down), Interlocked.Read(ref app.Counter.Up),
                    app.Connections.Count, app.Blocked, app.QuotaExceeded,
                    app.DownShaper.Rate, app.UpShaper.Rate));
            }

            _snapshot = new EngineSnapshot(now, apps, totalDown, totalUp);

            if (tick % 10 == 0) PruneProcesses();
            if (tick % 30 == 0) Stats.Save();

            Updated?.Invoke(_snapshot);
        }
        catch (Exception ex) { DataPaths.LogError("Erreur du minuteur", ex); }
    }

    private void PruneProcesses()
    {
        foreach (int pid in _byPid.Keys)
        {
            if (pid <= 4) continue;
            bool stale = !Win32.IsProcessAlive(pid);
            if (!stale && _processes.TryGetCached(pid, out var id) && id.Services.Length > 0)
            {
                // A svchost process can gain or lose services; re-resolve its identity if so.
                var now = ServiceCatalog.ForPid(pid).Select(s => s.Name)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
                stale = !now.SequenceEqual(id.Services, StringComparer.OrdinalIgnoreCase);
            }
            if (stale)
            {
                _byPid.TryRemove(pid, out _);
                _processes.Invalidate(pid);
            }
        }
    }

    private void ApplyAllRules()
    {
        var now = DateTime.Now;
        foreach (var app in _appList) ApplyRule(app, now);
    }

    private void ApplyRule(AppTraffic app, DateTime now)
    {
        // The app's own rule, one rule per hosted Windows service ("svc:Name"), and for a
        // service host the rule of its executable (a rule on svchost.exe covers all services).
        var keys = new List<string>(2 + app.Services.Length) { app.Path };
        if (app.ExePath != null) keys.Add(app.ExePath);
        foreach (var svc in app.Services)
        {
            string key = ServiceCatalog.KeyPrefix + svc;
            if (!string.Equals(key, app.Path, StringComparison.OrdinalIgnoreCase)) keys.Add(key);
        }

        bool blocked = false, quotaExceeded = false;
        long down = 0, up = 0;
        foreach (string key in keys)
        {
            var rule = Rules.Get(key);
            if (rule == null) continue;
            blocked |= rule.Blocked;
            if (rule.DownLimitEnabled) down = MinLimit(down, rule.DownLimitBytesPerSec);
            if (rule.UpLimitEnabled) up = MinLimit(up, rule.UpLimitBytesPerSec);

            if (!rule.Quota.Enabled) continue;
            var usage = Stats.GetPeriodUsage(key, rule.Quota.Period, now);
            long used = rule.Quota.Direction switch
            {
                QuotaDirection.Download => usage.Down,
                QuotaDirection.Upload => usage.Up,
                _ => usage.Total,
            };
            if (used < rule.Quota.LimitBytes) continue;
            quotaExceeded = true;
            if (rule.Quota.Action == QuotaAction.Block) blocked = true;
            else
            {
                long t = Math.Max(1024, rule.Quota.ThrottleBytesPerSec);
                down = MinLimit(down, t);
                up = MinLimit(up, t);
            }
        }

        app.QuotaExceeded = quotaExceeded;
        app.Blocked = blocked;
        app.DownShaper.SetRate(down);
        app.UpShaper.SetRate(up);
    }

    /// <summary>Most restrictive of two limits, where 0 means unlimited.</summary>
    private static long MinLimit(long current, long limit) => current == 0 ? limit : Math.Min(current, limit);
}
