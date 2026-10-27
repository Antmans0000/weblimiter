using System.Text.Json;
using WebLimiter.Core.Model;

namespace WebLimiter.Core.Storage;

/// <summary>Thread-safe, persisted set of per-application rules keyed by executable path.</summary>
public sealed class RuleStore
{
    private readonly object _lock = new();
    private Dictionary<string, AppRule> _rules = new(StringComparer.OrdinalIgnoreCase);

    public event Action? Changed;

    public static RuleStore Load()
    {
        var store = new RuleStore();
        try
        {
            if (File.Exists(DataPaths.Rules))
            {
                var list = JsonSerializer.Deserialize<List<AppRule>>(File.ReadAllText(DataPaths.Rules), DataPaths.Json);
                foreach (var r in list ?? new()) store._rules[r.AppPath] = r;
            }
        }
        catch (Exception ex) { DataPaths.LogError("Lecture des règles impossible", ex); }
        return store;
    }

    public AppRule? Get(string appPath)
    {
        lock (_lock) return _rules.TryGetValue(appPath, out var r) ? r.Clone() : null;
    }

    public IReadOnlyList<AppRule> All()
    {
        lock (_lock) return _rules.Values.Select(r => r.Clone()).ToList();
    }

    public void Set(AppRule rule)
    {
        lock (_lock)
        {
            if (rule.IsDefault) _rules.Remove(rule.AppPath);
            else _rules[rule.AppPath] = rule.Clone();
            Save();
        }
        Changed?.Invoke();
    }

    private void Save()
    {
        try { DataPaths.WriteAtomic(DataPaths.Rules, JsonSerializer.Serialize(_rules.Values.ToList(), DataPaths.Json)); }
        catch (Exception ex) { DataPaths.LogError("Écriture des règles impossible", ex); }
    }
}
