using System;
using System.Windows.Media;
using WebLimiter.App.Util;
using WebLimiter.Core.Engine;
using WebLimiter.Core.Model;
using WebLimiter.Core.Storage;

namespace WebLimiter.App.ViewModels;

/// <summary>One application row: live traffic (from the engine) plus its editable rule.</summary>
public sealed class AppRowViewModel : ObservableObject
{
    private static readonly string[] RuleProperties =
    {
        nameof(Blocked), nameof(DownLimitEnabled), nameof(DownLimitKb), nameof(UpLimitEnabled),
        nameof(UpLimitKb), nameof(HasRule), nameof(QuotaText), nameof(QuotaEnabled),
    };

    private readonly RuleBook _rules;
    private AppRule _shown;

    public AppRowViewModel(string path, string name, RuleBook rules)
    {
        Path = path;
        Name = name;
        _rules = rules;
        _shown = rules.GetOrDefault(path);
        Icon = IconCache.Get(path);
    }

    public string Path { get; }
    public string Name { get; }
    public ImageSource? Icon { get; }
    public History History { get; } = new();

    // ---- live values -----------------------------------------------------------------

    private double _downRate, _upRate;
    private long _todayDown, _todayUp;
    private int _connections;
    private bool _isActive, _engineBlocked, _quotaExceeded;

    public double DownRate { get => _downRate; private set { if (Set(ref _downRate, value)) { Raise(nameof(DownRateText)); Raise(nameof(TotalRate)); } } }
    public double UpRate { get => _upRate; private set { if (Set(ref _upRate, value)) { Raise(nameof(UpRateText)); Raise(nameof(TotalRate)); } } }
    public double TotalRate => _downRate + _upRate;
    public string DownRateText => Format.Rate(_downRate);
    public string UpRateText => Format.Rate(_upRate);

    public long TodayDown { get => _todayDown; private set { if (Set(ref _todayDown, value)) Raise(nameof(TodayDownText)); } }
    public long TodayUp { get => _todayUp; private set { if (Set(ref _todayUp, value)) Raise(nameof(TodayUpText)); } }
    public string TodayDownText => _todayDown > 0 ? Format.Bytes(_todayDown) : "";
    public string TodayUpText => _todayUp > 0 ? Format.Bytes(_todayUp) : "";

    public int Connections { get => _connections; private set => Set(ref _connections, value); }
    public bool IsActive { get => _isActive; private set => Set(ref _isActive, value); }
    public bool QuotaExceeded { get => _quotaExceeded; private set { if (Set(ref _quotaExceeded, value)) Raise(nameof(QuotaText)); } }
    public bool EngineBlocked { get => _engineBlocked; private set => Set(ref _engineBlocked, value); }

    public void Update(AppSnapshot? snap, AppUsage? today)
    {
        DownRate = snap?.DownRate ?? 0;
        UpRate = snap?.UpRate ?? 0;
        History.Push(DownRate, UpRate);
        Connections = snap?.Connections ?? 0;
        IsActive = snap != null && (snap.Connections > 0 || snap.DownRate > 0 || snap.UpRate > 0);
        EngineBlocked = snap?.Blocked ?? _shown.Blocked;
        QuotaExceeded = snap?.QuotaExceeded ?? false;
        TodayDown = today?.Down ?? 0;
        TodayUp = today?.Up ?? 0;
    }

    /// <summary>Re-reads the rule; notifies the view only if it changed, so a box being typed in is not reset.</summary>
    public void SyncRule()
    {
        var current = _rules.GetOrDefault(Path);
        if (current.SameAs(_shown)) return;
        _shown = current;
        foreach (var p in RuleProperties) Raise(p);
    }

    private void Edit(Action<AppRule> change) => _rules.Update(Path, change);

    // ---- rule (editable) --------------------------------------------------------------

    public bool Blocked { get => _shown.Blocked; set => Edit(r => r.Blocked = value); }
    public bool DownLimitEnabled { get => _shown.DownLimitEnabled; set => Edit(r => r.DownLimitEnabled = value); }
    public long DownLimitKb
    {
        get => _shown.DownLimitBytesPerSec / 1024;
        set => Edit(r => r.DownLimitBytesPerSec = Math.Max(1, value) * 1024);
    }
    public bool UpLimitEnabled { get => _shown.UpLimitEnabled; set => Edit(r => r.UpLimitEnabled = value); }
    public long UpLimitKb
    {
        get => _shown.UpLimitBytesPerSec / 1024;
        set => Edit(r => r.UpLimitBytesPerSec = Math.Max(1, value) * 1024);
    }

    public bool HasRule => !_shown.IsDefault;
    public bool QuotaEnabled => _shown.Quota.Enabled;

    public QuotaRule Quota
    {
        get => _shown.Quota.Clone();
        set => Edit(r => r.Quota = value.Clone());
    }

    public string QuotaText
    {
        get
        {
            var q = _shown.Quota;
            if (!q.Enabled) return "Aucun";
            string dir = q.Direction switch { QuotaDirection.Download => "↓ ", QuotaDirection.Upload => "↑ ", _ => "" };
            string per = q.Period == QuotaPeriod.Day ? "jour" : "mois";
            string text = $"{dir}{Format.Bytes(q.LimitBytes)} / {per}";
            return _quotaExceeded ? text + " — atteint" : text;
        }
    }
}
