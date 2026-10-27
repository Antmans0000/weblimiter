using System.Runtime.InteropServices;

namespace WebLimiter.Core.Native;

internal static class ServiceApi
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ScManagerEnumerateService = 0x0004;
    private const int ScEnumProcessInfo = 0;
    private const uint ServiceWin32 = 0x30;
    private const uint ServiceStateAll = 0x3;
    private const int ErrorMoreData = 234;
    private const int EntrySize = 56; // ENUM_SERVICE_STATUS_PROCESSW on x64

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool EnumServicesStatusEx(IntPtr scm, int infoLevel, uint serviceType, uint serviceState,
        IntPtr buffer, int bufSize, out int bytesNeeded, out int servicesReturned, ref int resumeHandle, string? group);

    /// <summary>(name, display name, state, pid) of every Win32 service.</summary>
    public static List<(string Name, string Display, int State, int Pid)> Enumerate()
    {
        var list = new List<(string, string, int, int)>(400);
        IntPtr scm = OpenSCManager(null, null, ScManagerConnect | ScManagerEnumerateService);
        if (scm == IntPtr.Zero)
        {
            Storage.DataPaths.LogError($"OpenSCManager a échoué (erreur {Marshal.GetLastWin32Error()})");
            return list;
        }
        try
        {
            int resume = 0;
            int size = 64 * 1024;
            while (true)
            {
                IntPtr buf = Marshal.AllocHGlobal(size);
                try
                {
                    bool ok = EnumServicesStatusEx(scm, ScEnumProcessInfo, ServiceWin32, ServiceStateAll,
                        buf, size, out int needed, out int count, ref resume, null);
                    int err = ok ? 0 : Marshal.GetLastWin32Error();
                    if (!ok && err != ErrorMoreData)
                    {
                        Storage.DataPaths.LogError($"EnumServicesStatusEx a échoué (erreur {err})");
                        return list;
                    }
                    for (int i = 0; i < count; i++)
                    {
                        IntPtr e = buf + i * EntrySize;
                        string name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(e)) ?? "";
                        string display = Marshal.PtrToStringUni(Marshal.ReadIntPtr(e + 8)) ?? name;
                        int state = Marshal.ReadInt32(e + 20);
                        int pid = Marshal.ReadInt32(e + 44);
                        list.Add((name, display, state, pid));
                    }
                    if (ok) return list;
                    size = Math.Max(size, needed + 4096);
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }
        finally { CloseServiceHandle(scm); }
    }
}
