namespace WebLimiter.Core.Engine;

public sealed record AppSnapshot(
    string Path,
    string Name,
    double DownRate,
    double UpRate,
    long SessionDown,
    long SessionUp,
    int Connections,
    bool Blocked,
    bool QuotaExceeded,
    long EffectiveDownLimit,
    long EffectiveUpLimit);

public sealed record ConnectionSnapshot(
    string Protocol,
    int LocalPort,
    string RemoteAddress,
    int RemotePort,
    double DownRate,
    double UpRate,
    long TotalDown,
    long TotalUp);

public sealed record EngineSnapshot(
    DateTime Time,
    IReadOnlyList<AppSnapshot> Apps,
    double TotalDownRate,
    double TotalUpRate,
    bool GlobalQuotaExceeded = false)
{
    public static readonly EngineSnapshot Empty = new(DateTime.Now, Array.Empty<AppSnapshot>(), 0, 0);
}
