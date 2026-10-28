using System.Buffers.Binary;
using System.Collections.Concurrent;
using WebLimiter.Core.Model;
using WebLimiter.Core.Native;
using WebLimiter.Core.Storage;

namespace WebLimiter.Core.Engine;

/// <summary>
/// Driverless engine for machines where WinDivert cannot load (Windows on ARM64): traffic is measured
/// from ETW kernel network events, blocking uses WFP filters and upload limits use QoS policies.
/// Download limits cannot be enforced.
/// </summary>
public sealed unsafe class EtwEngine : EngineBase
{
    private readonly QosLimiter _qos = new();
    private readonly Dictionary<string, List<ulong>> _blocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, EndpointBlock> _endpoints = new();
    private readonly object _enforceLock = new();
    private EtwSession? _etw;
    private WfpSession? _wfp;
    private Timer? _enforceTimer;
    private int _enforcing;
    private volatile bool _dirty = true;
    private const long EndpointIdleMs = 60_000;

    public EtwEngine(RuleStore rules, StatsStore stats) : base(rules, stats) { }

    public override void Start()
    {
        if (Running) return;
        _wfp = new WfpSession();
        try
        {
            _etw = new EtwSession(OnEvent);
            _etw.Start();
        }
        catch
        {
            _wfp.Dispose();
            _wfp = null;
            _etw?.Dispose();
            _etw = null;
            throw;
        }

        Running = true;
        Rules.Changed += MarkDirty;
        BeginTicking();
        _qos.ClearAll();
        _enforceTimer = new Timer(_ => Enforce(), null, 300, 300);
    }

    public override void Stop()
    {
        if (!Running) return;
        Running = false;
        Rules.Changed -= MarkDirty;
        EndTicking();
        _enforceTimer?.Dispose();
        _enforceTimer = null;

        _etw?.Dispose();
        _etw = null;
        lock (_enforceLock)
        {
            _wfp?.Dispose();
            _wfp = null;
            _blocks.Clear();
            _endpoints.Clear();
            _qos.ClearAll();
        }
        ResetSnapshot();
    }

    private void MarkDirty() => _dirty = true;

    internal override void OnRuleApplied(AppTraffic app) => _dirty = true;

    // ---- traffic (ETW Microsoft-Windows-Kernel-Network) ---------------------------------

    private void OnEvent(ushort id, byte* d, int length)
    {
        bool v6, tcp, outbound;
        switch (id)
        {
            case 10: (v6, tcp, outbound) = (false, true, true); break;
            case 11: (v6, tcp, outbound) = (false, true, false); break;
            case 26: (v6, tcp, outbound) = (true, true, true); break;
            case 27: (v6, tcp, outbound) = (true, true, false); break;
            case 42: (v6, tcp, outbound) = (false, false, true); break;
            case 43: (v6, tcp, outbound) = (false, false, false); break;
            case 58: (v6, tcp, outbound) = (true, false, true); break;
            case 59: (v6, tcp, outbound) = (true, false, false); break;
            default: return;
        }
        int addrLen = v6 ? 16 : 4;
        if (length < 8 + 2 * addrLen + 4) return;

        int pid = *(int*)d;
        int size = *(int*)(d + 4);
        var daddr = new ReadOnlySpan<byte>(d + 8, addrLen);
        var saddr = new ReadOnlySpan<byte>(d + 8 + addrLen, addrLen);
        ushort dport = BinaryPrimitives.ReadUInt16BigEndian(new ReadOnlySpan<byte>(d + 8 + 2 * addrLen, 2));
        ushort sport = BinaryPrimitives.ReadUInt16BigEndian(new ReadOnlySpan<byte>(d + 10 + 2 * addrLen, 2));

        // The kernel logs both directions from the connection's point of view:
        // d* is the remote end, s* the local one.
        var remoteBytes = daddr;
        ushort remotePort = dport, localPort = sport;

        UInt128 remote = v6 ? BinaryPrimitives.ReadUInt128BigEndian(remoteBytes) : BinaryPrimitives.ReadUInt32BigEndian(remoteBytes);
        if (IsLoopback(remote, v6)) return;
        Trace?.Invoke($"etw id={id} pid={pid} size={size} remote={remote}:{remotePort} local={localPort}");

        var app = AppFor(pid);
        var conn = app.Connections.GetOrAdd(new ConnKey(tcp ? (byte)6 : (byte)17, localPort, remote, remotePort, v6),
            static _ => new Counter());
        conn.LastSeen = Environment.TickCount64;
        app.Counter.Add(outbound, size);
        conn.Add(outbound, size);

        if (app.Blocked && app.Services.Length > 0 && app.ExePath != null) TrackEndpoint(app, remoteBytes, remotePort);
    }

    /// <summary>
    /// WFP cannot tell the services of a shared svchost apart (they connect while impersonating a user),
    /// so a blocked service gets its remote endpoints blocked for that executable as they are seen.
    /// </summary>
    private void TrackEndpoint(AppTraffic app, ReadOnlySpan<byte> address, ushort port)
    {
        string key = $"{app.ExePath}|{Convert.ToHexString(address)}|{port}";
        if (_endpoints.TryGetValue(key, out var existing))
        {
            existing.LastSeen = Environment.TickCount64;
            return;
        }
        _endpoints.TryAdd(key, new EndpointBlock(app.Path, app.ExePath!, address.ToArray(), port));
    }

    private sealed class EndpointBlock
    {
        public EndpointBlock(string appPath, string exe, byte[] address, ushort port)
        {
            AppPath = appPath;
            Exe = exe;
            Address = address;
            Port = port;
        }

        public string AppPath { get; }
        public string Exe { get; }
        public byte[] Address { get; }
        public ushort Port { get; }
        public volatile bool Applied;
        public List<ulong> Ids = new();
        public long LastSeen = Environment.TickCount64;
    }

    private static bool IsLoopback(UInt128 remote, bool v6) =>
        v6 ? remote == 1 : (uint)(remote >> 24) == 127;

    // ---- enforcement (WFP blocks, QoS upload limits) ------------------------------------

    private void Enforce()
    {
        if (Interlocked.Exchange(ref _enforcing, 1) == 1) return;
        try
        {
            lock (_enforceLock)
            {
                if (_wfp == null) return;
                if (_dirty)
                {
                    _dirty = false;
                    ReconcileBlocks();
                    _qos.Apply(DesiredUploadLimits());
                }
                ReconcileEndpoints();
            }
        }
        catch (Exception ex) { DataPaths.LogError("Application des règles", ex); }
        finally { Volatile.Write(ref _enforcing, 0); }
    }

    private void ReconcileEndpoints()
    {
        long now = Environment.TickCount64;
        foreach (var (key, ep) in _endpoints)
        {
            bool stillBlocked = Apps.TryGetValue(ep.AppPath, out var app) && app.Blocked;
            if (!stillBlocked || now - ep.LastSeen > EndpointIdleMs)
            {
                if (ep.Applied) _wfp!.Remove(ep.Ids);
                _endpoints.TryRemove(key, out _);
                continue;
            }
            if (ep.Applied) continue;
            ep.Applied = true; // a failure is not retried every tick
            try { ep.Ids = _wfp!.BlockEndpoint(ep.Exe, ep.Address, ep.Port); }
            catch (Exception ex) { DataPaths.LogError($"Blocage de destination impossible ({key})", ex); }
        }
    }

    private void ReconcileBlocks()
    {
        var desired = DesiredBlocks();
        foreach (var path in _blocks.Keys.Where(k => !desired.Contains(k)).ToList())
        {
            _wfp!.Remove(_blocks[path]);
            _blocks.Remove(path);
        }
        foreach (var path in desired)
        {
            if (_blocks.ContainsKey(path)) continue;
            try { _blocks[path] = _wfp!.BlockExe(path); }
            catch (Exception ex)
            {
                DataPaths.LogError($"Blocage impossible ({path})", ex);
                _blocks[path] = new List<ulong>(); // do not retry every tick
            }
        }
    }

    /// <summary>Executables blocked as a whole. Services of a shared host are handled per endpoint.</summary>
    private HashSet<string> DesiredBlocks()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in Rules.All())
            if (rule.Blocked && IsFile(rule.AppPath)) set.Add(rule.AppPath);
        foreach (var app in AppList)
            if (app.Blocked && app.Services.Length == 0 && IsFile(app.Path)) set.Add(app.Path);
        // A rule on svchost.exe itself blocks every service, as with the WinDivert engine.
        return set;
    }

    private static bool IsFile(string path) => Path.IsPathRooted(path) && File.Exists(path);

    private Dictionary<string, long> DesiredUploadLimits()
    {
        var limits = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        void Add(string path, long rate)
        {
            if (rate <= 0 || !IsFile(path)) return;
            limits[path] = limits.TryGetValue(path, out long cur) ? Math.Min(cur, rate) : rate;
        }

        foreach (var rule in Rules.All())
            if (rule.UpLimitEnabled) Add(rule.AppPath, rule.UpLimitBytesPerSec);
        foreach (var app in AppList)
            if (app.Services.Length == 0) Add(app.Path, app.UpShaper.Rate);
        return limits;
    }
}
