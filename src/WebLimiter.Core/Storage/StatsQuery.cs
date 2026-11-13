namespace WebLimiter.Core.Storage;

public enum StatsGranularity { Hour, Day, Month }

/// <summary>Traffic between <see cref="From"/> (inclusive) and <see cref="To"/> (exclusive), to the hour.</summary>
public sealed class StatsQuery
{
    public DateTime From { get; set; }
    public DateTime To { get; set; }
    public StatsGranularity Granularity { get; set; } = StatsGranularity.Day;
    /// <summary>Apps counted in <see cref="StatsResult.Series"/> and <see cref="StatsResult.Selected"/>; null = all.</summary>
    public List<string>? Apps { get; set; }
}

public sealed record StatsBucket(DateTime Start, long Down, long Up);

public sealed class StatsResult
{
    /// <summary>Every app with traffic in the range, largest first.</summary>
    public List<AppUsage> Apps { get; set; } = new();
    /// <summary>Volume of the queried apps per bucket, zero-filled, oldest first.</summary>
    public List<StatsBucket> Series { get; set; } = new();
    /// <summary>Volume and highest rates of the queried apps (of all traffic when no filter).</summary>
    public AppUsage Selected { get; set; } = new("*", 0, 0);
    /// <summary>Some days of the range have only daily totals (recorded before hourly statistics existed).</summary>
    public bool Approximate { get; set; }
    /// <summary>First day with hourly data, if any.</summary>
    public DateTime? HourlySince { get; set; }
}
