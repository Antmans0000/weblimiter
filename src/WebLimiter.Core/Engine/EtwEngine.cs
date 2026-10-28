using System.Buffers.Binary;
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
    private readonly object _enforceLock = new();
    private EtwSession? _etw;
    private WfpSession? _wfp;
    private Timer? _enforceTimer;
    private int _enforcing;
    private volatile bool _dirty = true;

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
        _enforceTimer = new Timer(_ => Enforce(), null, 500, 1000);
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
    }

    private static bool IsLoopback(UInt128 remote, bool v6) =>
        v6 ? remote == 1 : (uint)(remote >> 24) == 127;

    // ---- enforcement (WFP blocks, QoS upload limits) ------------------------------------

    private void Enforce()
    {
        if (!_dirty || Interlocked.Exchange(ref _enforcing, 1) == 1) return;
        try
        {
            _dirty = false;
            lock (_enforceLock)
            {
                if (_wfp == null) return;
                ReconcileBlocks();
                _qos.Apply(DesiredUploadLimits());
            }
        }
        catch (Exception ex) { DataPaths.LogError("Application des règles", ex); }
        finally { Volatile.Write(ref _enforcing, 0); }
    }

    private void ReconcileBlocks()
    {
        var desired = DesiredBlocks();
        foreach (var key in _blocks.Keys.Where(k => !desired.Contains(k)).ToList())
        {
            _wfp!.Remove(_blocks[key]);
            _blocks.Remove(key);
        }
        foreach (var key in desired)
        {
            if (_blocks.ContainsKey(key)) continue;
            try
            {
                int sep = key.IndexOf('|');
                string service = key[..sep];
                string exe = key[(sep + 1)..];
                _blocks[key] = _wfp!.Block(exe, service.Length == 0 ? null : service);
            }
            catch (Exception ex)
            {
                DataPaths.LogError($"Blocage impossible ({key})", ex);
                _blocks[key] = new List<ulong>(); // do not retry every second
            }
        }
    }

    /// <summary>Keys "service|exePath" ("|exePath" blocks every process of the executable).</summary>
    private HashSet<string> DesiredBlocks()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string svchost = Path.Combine(Environment.SystemDirectory, "svchost.exe");

        foreach (var rule in Rules.All())
            if (rule.Blocked) AddTargets(set, rule.AppPath, svchost);

        foreach (var app in AppList)
        {
            if (!app.Blocked) continue;
            if (app.Services.Length > 0)
            {
                string exe = app.ExePath ?? svchost;
                foreach (var svc in app.Services) set.Add($"{svc}|{exe}");
            }
            else if (File.Exists(app.Path)) set.Add("|" + app.Path);
        }
        return set;
    }

    private static void AddTargets(HashSet<string> set, string key, string svchost)
    {
        if (key.StartsWith(ServiceCatalog.KeyPrefix, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var svc in key[ServiceCatalog.KeyPrefix.Length..].Split('+', StringSplitOptions.RemoveEmptyEntries))
                set.Add($"{svc}|{svchost}");
        }
        else if (Path.IsPathRooted(key) && File.Exists(key)) set.Add("|" + key);
    }

    private Dictionary<string, long> DesiredUploadLimits()
    {
        var limits = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        void Add(string path, long rate)
        {
            if (rate <= 0 || !Path.IsPathRooted(path) || !File.Exists(path)) return;
            limits[path] = limits.TryGetValue(path, out long cur) ? Math.Min(cur, rate) : rate;
        }

        foreach (var rule in Rules.All())
            if (rule.UpLimitEnabled) Add(rule.AppPath, rule.UpLimitBytesPerSec);
        foreach (var app in AppList)
            if (app.Services.Length == 0) Add(app.Path, app.UpShaper.Rate);
        return limits;
    }
}
