using System.Collections.Concurrent;
using WebLimiter.Core.Model;

namespace WebLimiter.Core.Engine;

/// <summary>Display names for application keys (executable paths, services or pseudo-apps).</summary>
public static class AppNames
{
    private static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static string Display(string appPath) => Cache.GetOrAdd(appPath, path => path switch
    {
        AppRule.GlobalKey => "Total",
        ProcessResolver.SystemPath => "Système",
        ProcessResolver.UnknownPath => "Inconnu",
        _ when IsService(path) => "Service : " + string.Join(", ",
            ServiceNames(path).Select(ServiceCatalog.DisplayName)),
        _ when path.StartsWith('[') && path.EndsWith(']') => path[1..^1],
        _ => ProcessResolver.DisplayName(path),
    });

    public static bool IsService(string appPath) =>
        appPath.StartsWith(ServiceCatalog.KeyPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Service names in a key like "svc:BITS" or "svc:A+B".</summary>
    public static string[] ServiceNames(string appPath) =>
        IsService(appPath) ? appPath[ServiceCatalog.KeyPrefix.Length..].Split('+') : Array.Empty<string>();

    /// <summary>True for real executables (not services or pseudo-apps).</summary>
    public static bool IsExecutable(string appPath) =>
        !IsService(appPath) && appPath is not (ProcessResolver.SystemPath or ProcessResolver.UnknownPath or AppRule.GlobalKey)
        && !appPath.StartsWith('[');
}
