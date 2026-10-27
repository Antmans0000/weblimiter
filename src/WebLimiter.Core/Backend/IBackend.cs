using WebLimiter.Core.Engine;
using WebLimiter.Core.Model;
using WebLimiter.Core.Storage;

namespace WebLimiter.Core.Backend;

public sealed record BackendStatus(bool Connected, bool IsRunning, string? Error, bool IsService);

/// <summary>
/// What the UI needs from the filtering engine, whether it runs in-process
/// (<see cref="LocalBackend"/>) or in the WebLimiter Windows service (<see cref="PipeBackend"/>).
/// </summary>
public interface IBackend : IDisposable
{
    BackendStatus GetStatus();
    EngineSnapshot GetSnapshot();
    IReadOnlyList<ConnectionSnapshot> GetConnections(string appPath);
    IReadOnlyList<AppRule> GetRules();
    void SetRule(AppRule rule);
    /// <summary>Usage per app over a date range; from == null means since the beginning.</summary>
    IReadOnlyList<AppUsage> GetUsageByApp(DateTime? from, DateTime to);
    IReadOnlyList<DayUsage> GetDaily(DateTime from, DateTime to);
    void ClearStats();
    void SetFiltering(bool enabled);
}

/// <summary>Engine hosted in the current process (UI without service, or the service itself).</summary>
public sealed class LocalBackend : IBackend
{
    private readonly TrafficEngine _engine;
    private readonly bool _isService;
    private string? _error;

    public LocalBackend(bool isService = false)
    {
        _isService = isService;
        _engine = new TrafficEngine(RuleStore.Load(), StatsStore.Load());
    }

    public TrafficEngine Engine => _engine;

    public BackendStatus GetStatus() => new(true, _engine.IsRunning, _error, _isService);
    public EngineSnapshot GetSnapshot() => _engine.Snapshot;
    public IReadOnlyList<ConnectionSnapshot> GetConnections(string appPath) => _engine.GetConnections(appPath);
    public IReadOnlyList<AppRule> GetRules() => _engine.Rules.All();
    public void SetRule(AppRule rule) => _engine.Rules.Set(rule);
    public IReadOnlyList<AppUsage> GetUsageByApp(DateTime? from, DateTime to) => _engine.Stats.GetAllApps(from, to);
    public IReadOnlyList<DayUsage> GetDaily(DateTime from, DateTime to) => _engine.Stats.GetDaily(from, to);
    public void ClearStats() => _engine.Stats.Clear();

    public void SetFiltering(bool enabled)
    {
        if (!enabled)
        {
            _engine.Stop();
            return;
        }
        try
        {
            _engine.Start();
            _error = null;
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            DataPaths.LogError("Démarrage du moteur impossible", ex);
        }
    }

    public void Dispose()
    {
        _engine.Stop();
        _engine.Stats.Save();
    }
}
