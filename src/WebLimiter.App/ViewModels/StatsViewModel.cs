using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using WebLimiter.App.Util;
using WebLimiter.Core.Backend;
using WebLimiter.Core.Engine;
using WebLimiter.Core.Storage;

namespace WebLimiter.App.ViewModels;

public enum StatsPeriod { Today, Yesterday, Last7Days, ThisMonth, LastMonth, Last12Months }

public sealed record StatsRow(string Path, string Name, ImageSource? Icon, long Down, long Up, double Fraction)
{
    public long Total => Down + Up;
    public string DownText => Format.Bytes(Down);
    public string UpText => Format.Bytes(Up);
    public string TotalText => Format.Bytes(Total);
    public double BarWidth => Math.Max(2, Fraction * 160);
}

public sealed record DayBar(DateTime Day, long Down, long Up);

public sealed class StatsViewModel : ObservableObject
{
    private readonly IBackend _backend;

    public StatsViewModel(IBackend backend) => _backend = backend;

    private StatsPeriod _period = StatsPeriod.Today;
    public StatsPeriod Period
    {
        get => _period;
        set { if (Set(ref _period, value)) Refresh(); }
    }

    private IReadOnlyList<StatsRow> _rows = Array.Empty<StatsRow>();
    public IReadOnlyList<StatsRow> Rows { get => _rows; private set => Set(ref _rows, value); }

    private IReadOnlyList<DayBar> _days = Array.Empty<DayBar>();
    public IReadOnlyList<DayBar> Days { get => _days; private set => Set(ref _days, value); }

    private string _summary = "";
    public string Summary { get => _summary; private set => Set(ref _summary, value); }

    public (DateTime From, DateTime To) Range()
    {
        var today = DateTime.Today;
        var month = new DateTime(today.Year, today.Month, 1);
        return _period switch
        {
            StatsPeriod.Yesterday => (today.AddDays(-1), today.AddDays(-1)),
            StatsPeriod.Last7Days => (today.AddDays(-6), today),
            StatsPeriod.ThisMonth => (month, today),
            StatsPeriod.LastMonth => (month.AddMonths(-1), month.AddDays(-1)),
            StatsPeriod.Last12Months => (month.AddMonths(-11), today),
            _ => (today, today),
        };
    }

    public void Refresh()
    {
        var (from, to) = Range();
        IReadOnlyList<AppUsage> apps;
        IReadOnlyList<DayUsage> daily;
        // A single day is not worth a chart; show the last 14 days for context instead.
        var chartFrom = (to - from).TotalDays < 1 ? to.AddDays(-13) : from;
        try
        {
            apps = _backend.GetUsageByApp(from, to);
            daily = _backend.GetDaily(chartFrom, to);
        }
        catch { return; }
        long max = apps.Count > 0 ? Math.Max(1, apps[0].Total) : 1;
        Rows = apps.Select(a => new StatsRow(a.AppPath, AppNames.Display(a.AppPath), IconCache.Get(a.AppPath),
            a.Down, a.Up, a.Total / (double)max)).ToList();

        Days = daily.Select(d => new DayBar(d.Day, d.Down, d.Up)).ToList();

        long down = apps.Sum(a => a.Down), up = apps.Sum(a => a.Up);
        Summary = $"↓ {Format.Bytes(down)}    ↑ {Format.Bytes(up)}    Total {Format.Bytes(down + up)}    —    {apps.Count} applications";
    }

    public void Clear()
    {
        try { _backend.ClearStats(); }
        catch { /* service unreachable: nothing cleared */ }
        Refresh();
    }
}
