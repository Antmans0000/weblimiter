using Microsoft.Win32;
using WebLimiter.Core.Native;

namespace WebLimiter.Core.Engine;

public sealed record ServiceInfo(string Name, string DisplayName, int State, int Pid, int StartType, string ImagePath,
    string ServiceDll)
{
    /// <summary>True when the service is one of the Windows Update download components (detected by its DLL).</summary>
    public bool IsWindowsUpdate => ServiceCatalog.IsWindowsUpdateDll(ServiceDll);

    /// <summary>svchost-hosted services are identified per service, others by their executable.</summary>
    public bool IsSvchost => ImagePath.EndsWith("\\svchost.exe", StringComparison.OrdinalIgnoreCase);

    public string RuleKey => IsSvchost || ImagePath.Length == 0 ? ServiceCatalog.KeyPrefix + Name : ImagePath;

    public bool IsRunning => State == 4;

    public string StateText => State switch
    {
        1 => "Arrêté",
        2 => "Démarrage…",
        3 => "Arrêt…",
        4 => "En cours",
        7 => "Suspendu",
        _ => "—",
    };

    public string StartText => StartType switch
    {
        0 or 1 => "Système",
        2 => "Automatique",
        3 => "Manuel",
        4 => "Désactivé",
        _ => "—",
    };
}

/// <summary>Snapshot of the machine's Win32 services, refreshed at most every two seconds.</summary>
public static class ServiceCatalog
{
    public const string KeyPrefix = "svc:";

    /// <summary>
    /// DLLs implementing the Windows Update chain: the update agent, BITS, Delivery Optimization,
    /// the Update Orchestrator and the Update Medic. Matching on the DLL instead of the service
    /// name finds these services whatever their name or the system language.
    /// </summary>
    private static readonly HashSet<string> WindowsUpdateDlls = new(StringComparer.OrdinalIgnoreCase)
    {
        "wuaueng.dll", "qmgr.dll", "dosvc.dll", "usosvc.dll", "waasmedicsvc.dll",
    };

    internal static bool IsWindowsUpdateDll(string serviceDll) =>
        serviceDll.Length > 0 && WindowsUpdateDlls.Contains(Path.GetFileName(serviceDll));

    /// <summary>Services found on this machine that download Windows updates.</summary>
    public static IReadOnlyList<ServiceInfo> GetWindowsUpdateServices() =>
        GetAll().Where(s => s.IsWindowsUpdate).ToList();

    private static readonly object Lock = new();
    private static IReadOnlyList<ServiceInfo> _all = Array.Empty<ServiceInfo>();
    private static Dictionary<string, ServiceInfo> _byName = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<int, ServiceInfo[]> _byPid = new();
    private static long _refreshedAt = -1_000_000; // "long ago" without overflowing the subtraction

    public static IReadOnlyList<ServiceInfo> GetAll(bool force = false)
    {
        lock (Lock)
        {
            if (force || Environment.TickCount64 - _refreshedAt > 2000) Refresh();
            return _all;
        }
    }

    public static ServiceInfo? Get(string name)
    {
        GetAll();
        lock (Lock) return _byName.GetValueOrDefault(name);
    }

    /// <summary>Running services hosted by a process (refreshes the snapshot once if the pid is unknown).</summary>
    public static ServiceInfo[] ForPid(int pid)
    {
        lock (Lock)
        {
            if (Environment.TickCount64 - _refreshedAt > 2000) Refresh();
            if (_byPid.TryGetValue(pid, out var s)) return s;
            if (Environment.TickCount64 - _refreshedAt > 200) Refresh();
            return _byPid.GetValueOrDefault(pid) ?? Array.Empty<ServiceInfo>();
        }
    }

    public static string DisplayName(string serviceName) => Get(serviceName)?.DisplayName ?? serviceName;

    private static void Refresh()
    {
        var list = new List<ServiceInfo>();
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            foreach (var (name, display, state, pid) in ServiceApi.Enumerate())
            {
                int start = -1;
                string image = "", dll = "";
                using (var key = root?.OpenSubKey(name))
                {
                    if (key != null)
                    {
                        start = key.GetValue("Start") is int s ? s : -1;
                        image = ParseImagePath(key.GetValue("ImagePath", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string);
                        using var parameters = key.OpenSubKey("Parameters");
                        dll = (parameters?.GetValue("ServiceDll") ?? key.GetValue("ServiceDll")) as string ?? "";
                    }
                }
                list.Add(new ServiceInfo(name, display, state, pid, start, image, Environment.ExpandEnvironmentVariables(dll)));
            }
        }
        catch (Exception ex) { Storage.DataPaths.LogError("Énumération des services impossible", ex); }

        _all = list.OrderBy(s => s.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        _byName = list.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        _byPid = list.Where(s => s.Pid > 0).GroupBy(s => s.Pid).ToDictionary(g => g.Key, g => g.ToArray());
        _refreshedAt = Environment.TickCount64;
    }

    /// <summary>Extracts the executable from a service command line ("C:\x\a.exe" -k ..., %SystemRoot%\...).</summary>
    internal static string ParseImagePath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string s = Environment.ExpandEnvironmentVariables(raw.Trim());
        if (s.StartsWith('"'))
        {
            int end = s.IndexOf('"', 1);
            s = end > 0 ? s[1..end] : s.Trim('"');
        }
        else
        {
            int exe = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exe > 0) s = s[..(exe + 4)];
        }
        if (s.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
            s = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), s[12..]);
        else if (s.StartsWith(@"\??\")) s = s[4..];
        return s;
    }
}
