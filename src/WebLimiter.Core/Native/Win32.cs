using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace WebLimiter.Core.Native;

internal static class Win32
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint StillActive = 259;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("winmm.dll")]
    public static extern uint timeBeginPeriod(uint period);

    [DllImport("winmm.dll")]
    public static extern uint timeEndPeriod(uint period);

    public static string? GetProcessImagePath(int pid)
    {
        IntPtr h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
        }
        finally { CloseHandle(h); }
    }

    public static bool IsProcessAlive(int pid)
    {
        IntPtr h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (h == IntPtr.Zero) return false;
        try { return GetExitCodeProcess(h, out uint code) && code == StillActive; }
        finally { CloseHandle(h); }
    }

    // ---- IP Helper: port -> pid tables -------------------------------------------------

    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int TcpTableOwnerPidAll = 5;
    private const int UdpTableOwnerPid = 1;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);

    /// <summary>Enumerates (protocol, localPort, pid) for every TCP/UDP endpoint, IPv4 and IPv6.</summary>
    public static IEnumerable<(byte Proto, ushort Port, int Pid)> EnumeratePortOwners()
    {
        var result = new List<(byte, ushort, int)>(512);
        // Row sizes / local-port offset / pid offset: TCP4 24/8/20, TCP6 56/20/52, UDP4 12/4/8, UDP6 28/20/24.
        // The port is in network order in the low 16 bits.
        Read(true, AfInet, 24, 8, 20, 6, result);
        Read(true, AfInet6, 56, 20, 52, 6, result);
        Read(false, AfInet, 12, 4, 8, 17, result);
        Read(false, AfInet6, 28, 20, 24, 17, result);
        return result;
    }

    private static void Read(bool tcp, int af, int rowSize, int portOffset, int pidOffset, byte proto,
        List<(byte, ushort, int)> into)
    {
        int size = 0;
        _ = tcp ? GetExtendedTcpTable(IntPtr.Zero, ref size, false, af, TcpTableOwnerPidAll, 0)
                : GetExtendedUdpTable(IntPtr.Zero, ref size, false, af, UdpTableOwnerPid, 0);
        if (size <= 0) return;
        size += 4096;
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            uint rc = tcp ? GetExtendedTcpTable(buf, ref size, false, af, TcpTableOwnerPidAll, 0)
                          : GetExtendedUdpTable(buf, ref size, false, af, UdpTableOwnerPid, 0);
            if (rc != 0) return;
            int count = Marshal.ReadInt32(buf);
            IntPtr row = buf + 4;
            for (int i = 0; i < count; i++, row += rowSize)
            {
                uint rawPort = (uint)Marshal.ReadInt32(row + portOffset);
                ushort port = BinaryPrimitives.ReverseEndianness((ushort)(rawPort & 0xFFFF));
                int pid = Marshal.ReadInt32(row + pidOffset);
                into.Add((proto, port, pid));
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
    }
}
