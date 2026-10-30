using System.Collections.Concurrent;
using System.Diagnostics;
using WebLimiter.Core.Model;
using WebLimiter.Core.Native;
using WebLimiter.Core.Storage;

namespace WebLimiter.Core.Engine;

/// <summary>
/// Accounting, rules and quotas shared by the engines. A subclass supplies the traffic source
/// (WinDivert packets or ETW events) and enforces blocks and limits.
/// </summary>
public abstract class EngineBase : IDisposable
{
    private const long ConnectionIdleMs = 30_000;

    internal readonly ProcessResolver Processes = new();
    internal readonly ConcurrentDictionary<string, AppTraffic> Apps = new(StringComparer.OrdinalIgnoreCase);
    internal readonly ConcurrentDictionary<int, AppTraffic> ByPid = new();
    internal AppTraffic[] AppList = Array.Empty<AppTraffic>();
    /// <summary>Shapers of the global rule, crossed by every packet after the app's own shaper.</summary>
    internal readonly Shaper GlobalDown = new();
    internal readonly Shaper GlobalUp = new();
    private volatile bool _globalBlocked;
    private volatile bool _globalQuotaExceeded;

    private Timer? _timer;
    private readonly Stopwatch _tickClock = new();
    private long _tickCount;
    private volatile bool _running;
    private volatile EngineSnapshot _snapshot = EngineSnapshot.Empty;

    protected EngineBase(RuleStore rules, StatsStore stats)
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

    public abstract void Start();
    public abstract void Stop();

    public void Dispose() => Stop();

    protected bool Running
    {
        get => _running;
        set => _running = value;
    }

    protected void BeginTicking()
    {
        Rules.Changed += ApplyAllRules;
        _tickClock.Restart();
        _timer = new Timer(_ => Tick(), null, 1000, 1000);
    }

    protected void EndTicking()
    {
        Rules.Changed -= ApplyAllRules;
        _timer?.Dispose();
        _timer = null;
    }

    protected void ResetSnapshot()
    {
        Stats.Save();
        _snapshot = EngineSnapshot.Empty;
    }

    /// <summary>Called whenever the effective rule of an app was recomputed.</summary>
    internal virtual void OnRuleApplied(AppTraffic app) { }

    public IReadOnlyList<ConnectionSnapshot> GetConnections(string appPath)
    {
        if (!Apps.TryGetValue(appPath, out var app)) return Array.Empty<ConnectionSnapshot>();
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

    internal AppTraffic AppFor(int pid)
    {
        if (ByPid.TryGetValue(pid, out var app)) return app;
        var id = Processes.Resolve(pid);
        app = Apps.GetOrAdd(id.Path, path =>
        {
            var created = new AppTraffic(path, id.Name, id.Services, id.ExePath);
            ApplyRule(created, DateTime.Now);
            return created;
        });
        lock (Apps)
        {
            if (!AppList.Contains(app)) AppList = AppList.Append(app).ToArray();
        }
        ByPid[pid] = app;
        return app;
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
            ApplyGlobalRule(now);

            var apps = new List<AppSnapshot>(AppList.Length);
            double totalDown = 0, totalUp = 0;
            foreach (var app in AppList)
            {
                var (dd, du) = app.Counter.Tick(seconds);
                Stats.Add(app.Path, dd, du, app.Counter.DownRate, app.Counter.UpRate, now);

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

            Stats.Add(AppRule.GlobalKey, 0, 0, totalDown, totalUp, now);
            _snapshot = new EngineSnapshot(now, apps, totalDown, totalUp, _globalQuotaExceeded);

            if (tick % 10 == 0) PruneProcesses();
            if (tick % 30 == 0) Stats.Save();

            Updated?.Invoke(_snapshot);
        }
        catch (Exception ex) { DataPaths.LogError("Erreur du minuteur", ex); }
    }

    private void PruneProcesses()
    {
        foreach (int pid in ByPid.Keys)
        {
            if (pid <= 4) continue;
            bool stale = !Win32.IsProcessAlive(pid);
            if (!stale && Processes.TryGetCached(pid, out var id) && id.Services.Length > 0)
            {
                // A svchost process can gain or lose services; re-resolve its identity if so.
                var now = ServiceCatalog.ForPid(pid).Select(s => s.Name)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
                stale = !now.SequenceEqual(id.Services, StringComparer.OrdinalIgnoreCase);
            }
            if (stale)
            {
                ByPid.TryRemove(pid, out _);
                Processes.Invalidate(pid);
            }
        }
    }

    private void ApplyAllRules()
    {
        var now = DateTime.Now;
        ApplyGlobalRule(now);
        foreach (var app in AppList) ApplyRule(app, now);
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
            if (rule.Quota.Enabled)
                quotaExceeded |= ApplyQuota(rule.Quota, Stats.GetPeriodUsage(key, rule.Quota.Period, now),
                    ref blocked, ref down, ref up);
        }

        app.QuotaExceeded = quotaExceeded;
        app.Blocked = blocked || _globalBlocked;
        app.DownShaper.SetRate(down);
        app.UpShaper.SetRate(up);
        OnRuleApplied(app);
    }

    /// <summary>
    /// The global rule limits the sum of all traffic (shared shapers) and its quota counts every app;
    /// once reached it blocks or throttles all traffic together.
    /// </summary>
    private void ApplyGlobalRule(DateTime now)
    {
        var rule = Rules.Get(AppRule.GlobalKey);
        bool blocked = false, exceeded = false;
        long down = 0, up = 0;
        if (rule != null)
        {
            if (rule.DownLimitEnabled) down = rule.DownLimitBytesPerSec;
            if (rule.UpLimitEnabled) up = rule.UpLimitBytesPerSec;
            if (rule.Quota.Enabled)
                exceeded = ApplyQuota(rule.Quota, Stats.GetPeriodUsage(null, rule.Quota.Period, now),
                    ref blocked, ref down, ref up);
        }
        _globalBlocked = blocked;
        _globalQuotaExceeded = exceeded;
        GlobalDown.SetRate(down);
        GlobalUp.SetRate(up);
    }

    /// <summary>Applies the quota's action if <paramref name="usage"/> reached it; returns true if so.</summary>
    private static bool ApplyQuota(QuotaRule quota, Usage usage, ref bool blocked, ref long down, ref long up)
    {
        long used = quota.Direction switch
        {
            QuotaDirection.Download => usage.Down,
            QuotaDirection.Upload => usage.Up,
            _ => usage.Total,
        };
        if (used < quota.LimitBytes) return false;
        if (quota.Action == QuotaAction.Block) blocked = true;
        else
        {
            long t = Math.Max(1024, quota.ThrottleBytesPerSec);
            down = MinLimit(down, t);
            up = MinLimit(up, t);
        }
        return true;
    }

    /// <summary>Most restrictive of two limits, where 0 means unlimited.</summary>
    private static long MinLimit(long current, long limit) => current == 0 ? limit : Math.Min(current, limit);
}
