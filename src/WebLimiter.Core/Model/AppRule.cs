namespace WebLimiter.Core.Model;

public enum QuotaPeriod { Day, Month }

public enum QuotaDirection { Total, Download, Upload }

public enum QuotaAction { Block, Limit }

public sealed class QuotaRule
{
    public bool Enabled { get; set; }
    public QuotaPeriod Period { get; set; } = QuotaPeriod.Day;
    public QuotaDirection Direction { get; set; } = QuotaDirection.Total;
    public long LimitBytes { get; set; } = 1L << 30;
    public QuotaAction Action { get; set; } = QuotaAction.Block;
    /// <summary>Rate (bytes/s, both directions) applied once the quota is reached and Action == Limit.</summary>
    public long ThrottleBytesPerSec { get; set; } = 32 * 1024;

    public QuotaRule Clone() => (QuotaRule)MemberwiseClone();

    public bool SameAs(QuotaRule o) =>
        Enabled == o.Enabled && Period == o.Period && Direction == o.Direction && LimitBytes == o.LimitBytes
        && Action == o.Action && ThrottleBytesPerSec == o.ThrottleBytesPerSec;
}

/// <summary>User rule for one application, identified by its executable path.</summary>
public sealed class AppRule
{
    /// <summary>Key of the global rule: its limits and quota apply to the traffic of all applications together.</summary>
    public const string GlobalKey = "*";

    public string AppPath { get; set; } = "";
    public bool Blocked { get; set; }
    public bool DownLimitEnabled { get; set; }
    public long DownLimitBytesPerSec { get; set; } = 100 * 1024;
    public bool UpLimitEnabled { get; set; }
    public long UpLimitBytesPerSec { get; set; } = 100 * 1024;
    public QuotaRule Quota { get; set; } = new();

    public bool IsDefault =>
        !Blocked && !DownLimitEnabled && !UpLimitEnabled && !Quota.Enabled;

    public bool SameAs(AppRule o) =>
        string.Equals(AppPath, o.AppPath, StringComparison.OrdinalIgnoreCase) && Blocked == o.Blocked
        && DownLimitEnabled == o.DownLimitEnabled && DownLimitBytesPerSec == o.DownLimitBytesPerSec
        && UpLimitEnabled == o.UpLimitEnabled && UpLimitBytesPerSec == o.UpLimitBytesPerSec && Quota.SameAs(o.Quota);

    public AppRule Clone()
    {
        var c = (AppRule)MemberwiseClone();
        c.Quota = Quota.Clone();
        return c;
    }
}
