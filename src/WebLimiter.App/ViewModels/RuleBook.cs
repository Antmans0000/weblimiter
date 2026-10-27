using System;
using System.Collections.Generic;
using System.Linq;
using WebLimiter.Core.Backend;
using WebLimiter.Core.Model;

namespace WebLimiter.App.ViewModels;

/// <summary>UI-side copy of the backend's rules, shared by every tab.</summary>
public sealed class RuleBook
{
    private readonly IBackend _backend;
    private Dictionary<string, AppRule> _rules = new(StringComparer.OrdinalIgnoreCase);

    public RuleBook(IBackend backend) => _backend = backend;

    /// <summary>Raised after the rules changed (locally or in the backend).</summary>
    public event Action? Changed;

    public IEnumerable<AppRule> All => _rules.Values;

    public AppRule? Get(string key) => _rules.GetValueOrDefault(key);

    public AppRule GetOrDefault(string key) => _rules.TryGetValue(key, out var r) ? r.Clone() : new AppRule { AppPath = key };

    public void Update(string key, Action<AppRule> change)
    {
        var rule = GetOrDefault(key);
        change(rule);
        try { _backend.SetRule(rule); }
        catch (Exception ex) { Core.Storage.DataPaths.LogError("Enregistrement de la règle impossible", ex); return; }
        if (rule.IsDefault) _rules.Remove(key);
        else _rules[key] = rule;
        Changed?.Invoke();
    }

    /// <summary>Pulls the backend's rules; raises Changed only if something differs.</summary>
    public void Reload()
    {
        IReadOnlyList<AppRule> fresh;
        try { fresh = _backend.GetRules(); }
        catch { return; }
        bool same = fresh.Count == _rules.Count
                    && fresh.All(r => _rules.TryGetValue(r.AppPath, out var mine) && mine.SameAs(r));
        if (same) return;
        _rules = fresh.ToDictionary(r => r.AppPath, r => r, StringComparer.OrdinalIgnoreCase);
        Changed?.Invoke();
    }
}
