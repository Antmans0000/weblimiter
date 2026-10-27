using System.Collections.Concurrent;
using System.Diagnostics;
using WebLimiter.Core.Native;

namespace WebLimiter.Core.Engine;

/// <summary>
/// Path is the application key used for rules and stats: the executable path, or
/// "svc:Name" (several joined with '+') for a svchost process hosting Windows services.
/// </summary>
internal readonly record struct ProcessIdentity(string Path, string Name, string[] Services, string? ExePath = null);

/// <summary>Resolves a pid to its executable path / display name, with caching.</summary>
internal sealed class ProcessResolver
{
    public const string SystemPath = "System";
    public const string UnknownPath = "?";

    private readonly ConcurrentDictionary<int, ProcessIdentity> _cache = new();

    public ProcessIdentity Resolve(int pid)
    {
        if (pid < 0) return new ProcessIdentity(UnknownPath, "Inconnu", Array.Empty<string>());
        if (pid is 0 or 4) return new ProcessIdentity(SystemPath, "Système", Array.Empty<string>());
        return _cache.GetOrAdd(pid, Lookup);
    }

    private static ProcessIdentity Lookup(int pid)
    {
        string? path = Win32.GetProcessImagePath(pid);
        if (path == null)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                return new ProcessIdentity($"[{p.ProcessName}]", p.ProcessName, Array.Empty<string>());
            }
            catch { return new ProcessIdentity(UnknownPath, "Inconnu", Array.Empty<string>()); }
        }

        if (path.EndsWith("\\svchost.exe", StringComparison.OrdinalIgnoreCase))
        {
            var services = ServiceCatalog.ForPid(pid).Select(s => s.Name)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
            if (services.Length > 0)
            {
                string key = ServiceCatalog.KeyPrefix + string.Join('+', services);
                return new ProcessIdentity(key, AppNames.Display(key), services, path);
            }
        }
        return new ProcessIdentity(path, DisplayName(path), Array.Empty<string>());
    }

    public static string DisplayName(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            if (!string.IsNullOrWhiteSpace(info.FileDescription)) return info.FileDescription.Trim();
        }
        catch { /* fall back to file name */ }
        return Path.GetFileNameWithoutExtension(path);
    }

    public bool TryGetCached(int pid, out ProcessIdentity identity) => _cache.TryGetValue(pid, out identity);

    /// <summary>Forget a pid (its process exited, the id was reused, or its services changed).</summary>
    public void Invalidate(int pid) => _cache.TryRemove(pid, out _);
}
