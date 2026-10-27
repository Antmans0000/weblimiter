using System.Diagnostics;
using WebLimiter.Core.Native;

namespace WebLimiter.Core.Engine;

public static class ProcessCatalog
{
    /// <summary>Distinct executable paths of the running processes that can be queried.</summary>
    public static HashSet<string> RunningExecutables()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (p.Id <= 4) continue;
                string? path = Win32.GetProcessImagePath(p.Id);
                if (path != null) paths.Add(path);
            }
        }
        return paths;
    }
}
