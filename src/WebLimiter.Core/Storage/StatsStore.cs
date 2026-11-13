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
    private readonly HourlyStore _hourly = new();

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
        _hourly.Add(appPath, down, up, downRate, upRate, when);
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

    /// <summary>
    /// Traffic over an hour-precise range. Days recorded before hourly statistics existed only have
    /// daily totals: they are counted whole and the result is flagged <see cref="StatsResult.Approximate"/>.
    /// </summary>
    public StatsResult Query(StatsQuery q)
    {
        var from = new DateTime(q.From.Year, q.From.Month, q.From.Day, q.From.Hour, 0, 0);
        var to = q.To;
        var result = new StatsResult { HourlySince = _hourly.FirstDay() };
        if (to <= from) return result;

        HashSet<string>? filter = q.Apps is { Count: > 0 } ? new(q.Apps, StringComparer.OrdinalIgnoreCase) : null;
        var agg = new Dictionary<string, Usage>(StringComparer.OrdinalIgnoreCase);
        var buckets = new SortedDictionary<DateTime, long[]>();
        for (var t = BucketStart(from, q.Granularity); t < to; t = NextBucket(t, q.Granularity)) buckets[t] = new long[2];
        long selDown = 0, selUp = 0, selPeakDown = 0, selPeakUp = 0, allPeakDown = 0, allPeakUp = 0;

        void Count(string path, Usage u, DateTime when)
        {
            if (IsGlobal(path))
            {
                allPeakDown = Math.Max(allPeakDown, u.PeakDown);
                allPeakUp = Math.Max(allPeakUp, u.PeakUp);
                return;
            }
            if (!agg.TryGetValue(path, out var a)) agg[path] = a = new Usage();
            a.Down += u.Down;
            a.Up += u.Up;
            a.PeakDown = Math.Max(a.PeakDown, u.PeakDown);
            a.PeakUp = Math.Max(a.PeakUp, u.PeakUp);
            if (filter != null && !filter.Contains(path)) return;
            selDown += u.Down;
            selUp += u.Up;
            selPeakDown = Math.Max(selPeakDown, u.PeakDown);
            selPeakUp = Math.Max(selPeakUp, u.PeakUp);
            if (buckets.TryGetValue(BucketStart(when, q.Granularity), out var b))
            {
                b[0] += u.Down;
                b[1] += u.Up;
            }
        }

        for (var day = from.Date; day < to; day = day.AddDays(1))
        {
            Dictionary<string, Usage>? daily;
            lock (_lock) daily = _days.TryGetValue(DayKey(day), out var d) ? CloneDay(d) : null;
            var dayStart = day < from ? from : day;
            var hours = _hourly.Day(day);
            if (hours == null)
            {
                if (daily == null) continue;
                if (day < from || day.AddDays(1) > to || q.Granularity == StatsGranularity.Hour) result.Approximate = true;
                foreach (var (path, u) in daily) Count(path, u, dayStart);
                continue;
            }

            var hourlySum = new Dictionary<string, Usage>(StringComparer.OrdinalIgnoreCase);
            foreach (var (h, apps) in hours)
            {
                var t = day.AddHours(h);
                foreach (var (path, u) in apps)
                {
                    if (!hourlySum.TryGetValue(path, out var sum)) hourlySum[path] = sum = new Usage();
                    sum.Down += u.Down;
                    sum.Up += u.Up;
                    if (t >= from && t < to) Count(path, u, t);
                }
            }
            // The day hourly statistics started also holds traffic counted earlier that day, hour unknown.
            if (daily == null) continue;
            foreach (var (path, u) in daily)
            {
                if (IsGlobal(path)) continue;
                var sum = hourlySum.GetValueOrDefault(path);
                long down = u.Down - (sum?.Down ?? 0), up = u.Up - (sum?.Up ?? 0);
                if (down + up < 64 * 1024) continue;
                result.Approximate = true;
                Count(path, new Usage { Down = Math.Max(0, down), Up = Math.Max(0, up) }, dayStart);
            }
        }

        result.Apps = agg.Select(kv => new AppUsage(kv.Key, kv.Value.Down, kv.Value.Up, kv.Value.PeakDown, kv.Value.PeakUp))
            .OrderByDescending(a => a.Total).ToList();
        result.Series = buckets.Select(kv => new StatsBucket(kv.Key, kv.Value[0], kv.Value[1])).ToList();
        result.Selected = filter == null
            ? new AppUsage(AppRule.GlobalKey, selDown, selUp, allPeakDown, allPeakUp)
            : new AppUsage(AppRule.GlobalKey, selDown, selUp, selPeakDown, selPeakUp);
        return result;
    }

    private static Dictionary<string, Usage> CloneDay(Dictionary<string, Usage> day) =>
        day.ToDictionary(kv => kv.Key, kv => new Usage { Down = kv.Value.Down, Up = kv.Value.Up, PeakDown = kv.Value.PeakDown, PeakUp = kv.Value.PeakUp },
            StringComparer.OrdinalIgnoreCase);

    public static DateTime BucketStart(DateTime t, StatsGranularity g) => g switch
    {
        StatsGranularity.Hour => new DateTime(t.Year, t.Month, t.Day, t.Hour, 0, 0),
        StatsGranularity.Month => new DateTime(t.Year, t.Month, 1),
        _ => t.Date,
    };

    public static DateTime NextBucket(DateTime t, StatsGranularity g) => g switch
    {
        StatsGranularity.Hour => t.AddHours(1),
        StatsGranularity.Month => t.AddMonths(1),
        _ => t.AddDays(1),
    };

    public void Save()
    {
        _hourly.Save();
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
        _hourly.Clear();
        Save();
    }
}
