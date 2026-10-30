using System.Text.Json;
using WebLimiter.Core.Model;

namespace WebLimiter.Core.Storage;

public sealed class Usage
{
    public long Down { get; set; }
    public long Up { get; set; }
    /// <summary>Highest per-second rates seen that day (bytes/s).</summary>
    public long PeakDown { get; set; }
    public long PeakUp { get; set; }
    public long Total => Down + Up;
}

/// <summary>Volume and highest rates (bytes/s) of one app over a period.</summary>
public sealed record AppUsage(string AppPath, long Down, long Up, long PeakDown = 0, long PeakUp = 0)
{
    public long Total => Down + Up;
}

public sealed record DayUsage(DateTime Day, long Down, long Up);

/// <summary>
/// Daily per-application traffic totals and peak rates, persisted to JSON. The entry keyed
/// <see cref="AppRule.GlobalKey"/> only holds the peak rates of all traffic together.
/// </summary>
public sealed class StatsStore
{
    private readonly object _lock = new();
    // day "yyyy-MM-dd" -> app path -> usage
    private Dictionary<string, Dictionary<string, Usage>> _days = new();
    private bool _dirty;

    public static StatsStore Load()
    {
        var store = new StatsStore();
        try
        {
            if (File.Exists(DataPaths.Stats))
            {
                var data = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, Usage>>>(
                    File.ReadAllText(DataPaths.Stats), DataPaths.Json);
                if (data != null)
                    store._days = data.ToDictionary(d => d.Key,
                        d => new Dictionary<string, Usage>(d.Value, StringComparer.OrdinalIgnoreCase));
            }
        }
        catch (Exception ex) { DataPaths.LogError("Lecture des statistiques impossible", ex); }
        return store;
    }

    private static string DayKey(DateTime d) => d.ToString("yyyy-MM-dd");

    public void Add(string appPath, long down, long up, double downRate, double upRate, DateTime when)
    {
        if (down == 0 && up == 0 && downRate < 1 && upRate < 1) return;
        lock (_lock)
        {
            string key = DayKey(when);
            if (!_days.TryGetValue(key, out var apps))
                _days[key] = apps = new Dictionary<string, Usage>(StringComparer.OrdinalIgnoreCase);
            if (!apps.TryGetValue(appPath, out var u)) apps[appPath] = u = new Usage();
            u.Down += down;
            u.Up += up;
            u.PeakDown = Math.Max(u.PeakDown, (long)downRate);
            u.PeakUp = Math.Max(u.PeakUp, (long)upRate);
            _dirty = true;
        }
    }

    private static bool IsGlobal(string key) => key == AppRule.GlobalKey;

    /// <summary>Usage of one app, or of all apps when appPath is null, between two dates (inclusive).</summary>
    public Usage GetUsage(string? appPath, DateTime from, DateTime to)
    {
        var total = new Usage();
        lock (_lock)
        {
            for (var d = from.Date; d <= to.Date; d = d.AddDays(1))
            {
                if (!_days.TryGetValue(DayKey(d), out var apps)) continue;
                foreach (var (path, u) in apps)
                {
                    if (IsGlobal(path)) continue;
                    if (appPath != null && !string.Equals(path, appPath, StringComparison.OrdinalIgnoreCase)) continue;
                    total.Down += u.Down;
                    total.Up += u.Up;
                }
            }
        }
        return total;
    }

    public Usage GetPeriodUsage(string? appPath, QuotaPeriod period, DateTime now) =>
        period == QuotaPeriod.Day
            ? GetUsage(appPath, now, now)
            : GetUsage(appPath, new DateTime(now.Year, now.Month, 1), now);

    /// <summary>All apps aggregated over a date range; from == null means since the beginning.</summary>
    public List<AppUsage> GetAllApps(DateTime? from, DateTime to)
    {
        var agg = new Dictionary<string, Usage>(StringComparer.OrdinalIgnoreCase);
        lock (_lock)
        {
            string fromKey = from.HasValue ? DayKey(from.Value) : "";
            string toKey = DayKey(to);
            foreach (var (day, apps) in _days)
            {
                if (string.CompareOrdinal(day, fromKey) < 0 || string.CompareOrdinal(day, toKey) > 0) continue;
                foreach (var (path, u) in apps)
                {
                    if (!agg.TryGetValue(path, out var a)) agg[path] = a = new Usage();
                    a.Down += u.Down;
                    a.Up += u.Up;
                    a.PeakDown = Math.Max(a.PeakDown, u.PeakDown);
                    a.PeakUp = Math.Max(a.PeakUp, u.PeakUp);
                }
            }
        }
        return agg.Where(kv => !IsGlobal(kv.Key))
                  .Select(kv => new AppUsage(kv.Key, kv.Value.Down, kv.Value.Up, kv.Value.PeakDown, kv.Value.PeakUp))
                  .OrderByDescending(a => a.Total).ToList();
    }

    /// <summary>All traffic over a date range: summed volumes and the highest total rates.</summary>
    public AppUsage GetTotal(DateTime? from, DateTime to)
    {
        long down = 0, up = 0, peakDown = 0, peakUp = 0;
        lock (_lock)
        {
            string fromKey = from.HasValue ? DayKey(from.Value) : "";
            string toKey = DayKey(to);
            foreach (var (day, apps) in _days)
            {
                if (string.CompareOrdinal(day, fromKey) < 0 || string.CompareOrdinal(day, toKey) > 0) continue;
                foreach (var (path, u) in apps)
                {
                    if (IsGlobal(path))
                    {
                        peakDown = Math.Max(peakDown, u.PeakDown);
                        peakUp = Math.Max(peakUp, u.PeakUp);
                        continue;
                    }
                    down += u.Down;
                    up += u.Up;
                }
            }
        }
        return new AppUsage(AppRule.GlobalKey, down, up, peakDown, peakUp);
    }

    /// <summary>Per-day totals (all apps or one app) for the given range, oldest first.</summary>
    public List<DayUsage> GetDaily(DateTime from, DateTime to, string? appPath = null)
    {
        var list = new List<DayUsage>();
        lock (_lock)
        {
            for (var d = from.Date; d <= to.Date; d = d.AddDays(1))
            {
                long down = 0, up = 0;
                if (_days.TryGetValue(DayKey(d), out var apps))
                {
                    foreach (var (path, u) in apps)
                    {
                        if (IsGlobal(path)) continue;
                        if (appPath != null && !string.Equals(path, appPath, StringComparison.OrdinalIgnoreCase)) continue;
                        down += u.Down;
                        up += u.Up;
                    }
                }
                list.Add(new DayUsage(d, down, up));
            }
        }
        return list;
    }

    public void Save()
    {
        string json;
        lock (_lock)
        {
            if (!_dirty) return;
            json = JsonSerializer.Serialize(_days, DataPaths.Json);
            _dirty = false;
        }
        try { DataPaths.WriteAtomic(DataPaths.Stats, json); }
        catch (Exception ex) { DataPaths.LogError("Écriture des statistiques impossible", ex); }
    }

    public void Clear()
    {
        lock (_lock) { _days.Clear(); _dirty = true; }
        Save();
    }
}
