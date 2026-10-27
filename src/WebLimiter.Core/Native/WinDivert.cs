using System.Runtime.InteropServices;

namespace WebLimiter.Core.Native;

internal enum WinDivertLayer
{
    Network = 0,
    NetworkForward = 1,
    Flow = 2,
    Socket = 3,
    Reflect = 4,
}

internal enum WinDivertEvent : byte
{
    NetworkPacket = 0,
    FlowEstablished = 1,
    FlowDeleted = 2,
    SocketBind = 3,
    SocketConnect = 4,
    SocketListen = 5,
    SocketAccept = 6,
    SocketClose = 7,
}

[Flags]
internal enum WinDivertFlags : ulong
{
    None = 0,
    Sniff = 0x0001,
    Drop = 0x0002,
    RecvOnly = 0x0004,
    SendOnly = 0x0008,
    NoInstall = 0x0010,
    Fragments = 0x0020,
}

internal enum WinDivertParam
{
    QueueLength = 0,
    QueueTime = 1,
    QueueSize = 2,
    VersionMajor = 3,
    VersionMinor = 4,
}

/// <summary>Mirror of WINDIVERT_ADDRESS (80 bytes) from windivert.h 2.2.</summary>
[StructLayout(LayoutKind.Explicit, Size = 80)]
internal struct WinDivertAddress
{
    [FieldOffset(0)] public long Timestamp;
    [FieldOffset(8)] public uint Bits;
    [FieldOffset(12)] public uint Reserved2;

    // NETWORK layer
    [FieldOffset(16)] public uint IfIdx;
    [FieldOffset(20)] public uint SubIfIdx;

    // FLOW / SOCKET layer
    [FieldOffset(16)] public ulong EndpointId;
    [FieldOffset(24)] public ulong ParentEndpointId;
    [FieldOffset(32)] public uint ProcessId;
    [FieldOffset(36)] public uint LocalAddr0;
    [FieldOffset(40)] public uint LocalAddr1;
    [FieldOffset(44)] public uint LocalAddr2;
    [FieldOffset(48)] public uint LocalAddr3;
    [FieldOffset(52)] public uint RemoteAddr0;
    [FieldOffset(56)] public uint RemoteAddr1;
    [FieldOffset(60)] public uint RemoteAddr2;
    [FieldOffset(64)] public uint RemoteAddr3;
    [FieldOffset(68)] public ushort LocalPort;
    [FieldOffset(70)] public ushort RemotePort;
    [FieldOffset(72)] public byte Protocol;

    public readonly WinDivertLayer Layer => (WinDivertLayer)(Bits & 0xFF);
    public readonly WinDivertEvent Event => (WinDivertEvent)((Bits >> 8) & 0xFF);
    public readonly bool Outbound => (Bits & (1u << 17)) != 0;
    public readonly bool Loopback => (Bits & (1u << 18)) != 0;
    public readonly bool IPv6 => (Bits & (1u << 20)) != 0;
}

internal static unsafe class WinDivertNative
{
    private const string Dll = "WinDivert.dll";

    public const int BatchMax = 0xFF;
    public const int ErrorNoData = 232;
    public const int ErrorOperationAborted = 995;
    public const int ErrorInvalidHandle = 6;

    public static readonly IntPtr InvalidHandle = new(-1);

    [DllImport(Dll, SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false)]
    public static extern IntPtr WinDivertOpen(string filter, WinDivertLayer layer, short priority, WinDivertFlags flags);

    [DllImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinDivertRecvEx(IntPtr handle, byte* pPacket, uint packetLen, out uint recvLen,
        ulong flags, WinDivertAddress* pAddr, ref uint pAddrLen, IntPtr overlapped);

    [DllImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinDivertSend(IntPtr handle, byte* pPacket, uint packetLen, out uint sendLen,
        WinDivertAddress* pAddr);

    [DllImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinDivertSetParam(IntPtr handle, WinDivertParam param, ulong value);

    [DllImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinDivertShutdown(IntPtr handle, uint how);

    [DllImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinDivertClose(IntPtr handle);

    /// <summary>Parses one packet of a batch; header pointers point inside pPacket.</summary>
    [DllImport(Dll)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinDivertHelperParsePacket(byte* pPacket, uint packetLen,
        byte** ppIpHdr, byte** ppIpv6Hdr, byte* pProtocol, void* ppIcmpHdr, void* ppIcmpv6Hdr,
        byte** ppTcpHdr, byte** ppUdpHdr, void* ppData, void* pDataLen, byte** ppNext, uint* pNextLen);
}
