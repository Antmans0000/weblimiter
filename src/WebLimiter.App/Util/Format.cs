using System.Globalization;

namespace WebLimiter.App.Util;

public static class Format
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    public static string Bytes(double bytes)
    {
        if (bytes < 1024) return $"{bytes:0} o";
        if (bytes < 1024 * 1024) return (bytes / 1024).ToString("0.0", Fr) + " Ko";
        if (bytes < 1024d * 1024 * 1024) return (bytes / 1024 / 1024).ToString("0.0", Fr) + " Mo";
        return (bytes / 1024 / 1024 / 1024).ToString("0.00", Fr) + " Go";
    }

    public static string Rate(double bytesPerSec) => bytesPerSec < 1 ? "-" : Bytes(bytesPerSec) + "/s";

    public static string Kb(long bytesPerSec) => (bytesPerSec / 1024).ToString(Fr) + " Ko/s";
}
