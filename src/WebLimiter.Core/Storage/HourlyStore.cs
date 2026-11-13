using System.Globalization;
using System.Text.Json;

namespace WebLimiter.Core.Storage;

/// <summary>
/// Per-hour, per-application traffic, one JSON file per day in <see cref="DataPaths.Hourly"/>.
/// Only the current day is written; past days are read on demand and cached. Files older than
/// <see cref="RetentionDays"/> are deleted.
/// </summary>
internal sealed class HourlyStore
{
    public const int RetentionDays = 90;

    private readonly object _lock = new();
    // day -> hour (0-23) -> app path -> usage
    private readonly Dictionary<DateTime, Dictionary<int, Dictionary<string, Usage>>> _days = new();
    private readonly HashSet<DateTime> _dirty = new();
    private DateTime _pruned;

    private static string FileFor(DateTime day) =>
        Path.Combine(DataPaths.Hourly, day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".json");

    private static bool TryParseDay(string file, out DateTime day) =>
        DateTime.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out day);

    public void Add(string appPath, long down, long up, double downRate, double upRate, DateTime when)
    {
        lock (_lock)
        {
            var day = when.Date;
            var hours = DayLocked(day) ?? (_days[day] = new());
            if (!hours.TryGetValue(when.Hour, out var apps))
                hours[when.Hour] = apps = new Dictionary<string, Usage>(StringComparer.OrdinalIgnoreCase);
            if (!apps.TryGetValue(appPath, out var u)) apps[appPath] = u = new Usage();
            u.Down += down;
            u.Up += up;
            u.PeakDown = Math.Max(u.PeakDown, (long)downRate);
            u.PeakUp = Math.Max(u.PeakUp, (long)upRate);
            _dirty.Add(day);
        }
    }

    /// <summary>The hours recorded for a day, or null if that day has no hourly data.</summary>
    public IReadOnlyDictionary<int, Dictionary<string, Usage>>? Day(DateTime day)
    {
        lock (_lock)
        {
            // A copy: the engine keeps adding to the current day while the caller reads it.
            return DayLocked(day.Date)?.ToDictionary(h => h.Key, h => h.Value.ToDictionary(
                a => a.Key, a => new Usage { Down = a.Value.Down, Up = a.Value.Up, PeakDown = a.Value.PeakDown, PeakUp = a.Value.PeakUp },
                StringComparer.OrdinalIgnoreCase));
        }
    }

    /// <summary>Oldest day with hourly data, or null if there is none.</summary>
    public DateTime? FirstDay()
    {
        lock (_lock)
        {
            DateTime? first = _days.Count > 0 ? _days.Keys.Min() : null;
            try
            {
                if (Directory.Exists(DataPaths.Hourly))
                    foreach (var f in Directory.EnumerateFiles(DataPaths.Hourly, "*.json"))
                        if (TryParseDay(f, out var d) && (first == null || d < first))
                            first = d;
            }
            catch (IOException) { }
            return first;
        }
    }

    private Dictionary<int, Dictionary<string, Usage>>? DayLocked(DateTime day)
    {
        if (_days.TryGetValue(day, out var hours)) return hours;
        string file = FileFor(day);
        if (!File.Exists(file)) return null;
        try
        {
            var data = JsonSerializer.Deserialize<Dictionary<int, Dictionary<string, Usage>>>(File.ReadAllText(file), DataPaths.Json);
            hours = (data ?? new()).ToDictionary(h => h.Key,
                h => new Dictionary<string, Usage>(h.Value, StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            DataPaths.LogError($"Lecture des statistiques horaires impossible ({file})", ex);
            hours = new();
        }
        _days[day] = hours;
        return hours;
    }

    public void Save()
    {
        List<(string File, string Json)> writes;
        lock (_lock)
        {
            writes = _dirty.Select(d => (FileFor(d), JsonSerializer.Serialize(_days[d], DataPaths.Json))).ToList();
            _dirty.Clear();
        }
        foreach (var (file, json) in writes)
        {
            try { DataPaths.WriteAtomic(file, json); }
            catch (Exception ex) { DataPaths.LogError("Écriture des statistiques horaires impossible", ex); }
        }
        Prune();
    }

    private void Prune()
    {
        if (_pruned == DateTime.Today) return;
        _pruned = DateTime.Today;
        try
        {
            if (!Directory.Exists(DataPaths.Hourly)) return;
            var limit = DateTime.Today.AddDays(-RetentionDays);
            foreach (var f in Directory.EnumerateFiles(DataPaths.Hourly, "*.json"))
                if (TryParseDay(f, out var d) && d < limit)
                    File.Delete(f);
        }
        catch (Exception ex) { DataPaths.LogError("Nettoyage des statistiques horaires impossible", ex); }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _days.Clear();
            _dirty.Clear();
            try
            {
                if (Directory.Exists(DataPaths.Hourly))
                    foreach (var f in Directory.EnumerateFiles(DataPaths.Hourly, "*.json")) File.Delete(f);
            }
            catch (Exception ex) { DataPaths.LogError("Effacement des statistiques horaires impossible", ex); }
        }
    }
}
