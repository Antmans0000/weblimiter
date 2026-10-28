using System.Runtime.InteropServices;

namespace WebLimiter.Core.Native;

/// <summary>
/// Windows Filtering Platform user-mode API: block filters per executable, or per executable and
/// remote endpoint, in a dynamic session so every filter disappears if the process exits.
/// </summary>
internal sealed unsafe class WfpSession : IDisposable
{
    // ALE_AUTH_CONNECT_V4, ALE_AUTH_RECV_ACCEPT_V4, ALE_AUTH_CONNECT_V6, ALE_AUTH_RECV_ACCEPT_V6
    private static readonly Guid[] LayersV4 =
    {
        new("c38d57d1-05a7-4c33-904f-7fbceee60e82"),
        new("e1cd9fe7-f4b5-4273-96c0-592e487b8650"),
    };
    private static readonly Guid[] LayersV6 =
    {
        new("4a72393b-319f-44bc-84c3-ba54dcb3b6b4"),
        new("a3b42c97-9f04-4672-b87e-cee9c483257f"),
    };
    private static readonly Guid ConditionAppId = new("d78e1e87-8644-4ea5-9437-d809ecefc971");
    private static readonly Guid ConditionRemoteAddress = new("b235ae9a-1d64-49b8-a44c-5ff3d9095045");
    private static readonly Guid ConditionRemotePort = new("c35a604d-d22b-4e1a-91b4-68f674ee674b");

    private IntPtr _engine;

    public WfpSession()
    {
        var session = new Session0 { Flags = 1 }; // FWPM_SESSION_FLAG_DYNAMIC
        int rc = FwpmEngineOpen0(null, 10, IntPtr.Zero, &session, out _engine);
        if (rc != 0) throw new InvalidOperationException($"FwpmEngineOpen0 a échoué (0x{rc:X}).");
    }

    /// <summary>Blocks all traffic of an executable (IPv4 and IPv6, connect and accept).</summary>
    public List<ulong> BlockExe(string exePath) => Add(exePath, null, 0);

    /// <summary>
    /// Blocks one remote endpoint for an executable. <paramref name="address"/> is 4 bytes (IPv4) or
    /// 16 bytes (IPv6), in network order.
    /// </summary>
    public List<ulong> BlockEndpoint(string exePath, byte[] address, ushort port) => Add(exePath, address, port);

    private List<ulong> Add(string exePath, byte[]? address, ushort port)
    {
        var ids = new List<ulong>();
        int rc = FwpmGetAppIdFromFileName0(exePath, out IntPtr appId);
        if (rc != 0) throw new InvalidOperationException($"App id introuvable pour {exePath} (0x{rc:X}).");

        byte* addr16 = (byte*)NativeMemory.AllocZeroed(16);
        var name = Marshal.StringToHGlobalUni("WebLimiter");
        try
        {
            var conditions = stackalloc Condition0[3];
            conditions[0] = new Condition0
            {
                FieldKey = ConditionAppId,
                MatchType = 0,
                Value = new Value0 { Type = 12, Data = (ulong)appId }, // FWP_BYTE_BLOB_TYPE
            };
            uint count = 1;
            bool v6 = address is { Length: 16 };
            if (address != null)
            {
                Value0 value;
                if (v6)
                {
                    for (int i = 0; i < 16; i++) addr16[i] = address[i];
                    value = new Value0 { Type = 11, Data = (ulong)addr16 }; // FWP_BYTE_ARRAY16_TYPE
                }
                else
                {
                    uint host = (uint)(address[0] << 24 | address[1] << 16 | address[2] << 8 | address[3]);
                    value = new Value0 { Type = 3, Data = host };            // FWP_UINT32, host order
                }
                conditions[1] = new Condition0 { FieldKey = ConditionRemoteAddress, MatchType = 0, Value = value };
                conditions[2] = new Condition0
                {
                    FieldKey = ConditionRemotePort,
                    MatchType = 0,
                    Value = new Value0 { Type = 2, Data = port },            // FWP_UINT16
                };
                count = 3;
            }

            // An executable-wide block covers both families; an endpoint block only its own.
            var layers = address == null ? LayersV4.Concat(LayersV6).ToArray() : v6 ? LayersV6 : LayersV4;
            foreach (var layer in layers)
            {
                var filter = new Filter0
                {
                    DisplayName = name,
                    LayerKey = layer,
                    NumConditions = count,
                    Conditions = (IntPtr)conditions,
                    ActionType = 0x1001, // FWP_ACTION_BLOCK
                };
                rc = FwpmFilterAdd0(_engine, &filter, IntPtr.Zero, out ulong id);
                if (rc != 0)
                {
                    Remove(ids);
                    throw new InvalidOperationException($"FwpmFilterAdd0 a échoué (0x{rc:X}).");
                }
                ids.Add(id);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(name);
            NativeMemory.Free(addr16);
            FwpmFreeMemory0(&appId);
        }
        return ids;
    }

    public void Remove(IEnumerable<ulong> ids)
    {
        foreach (var id in ids) FwpmFilterDeleteById0(_engine, id);
    }

    public void Dispose()
    {
        if (_engine == IntPtr.Zero) return;
        FwpmEngineClose0(_engine); // dynamic session: all filters go away
        _engine = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Session0
    {
        public Guid SessionKey;
        public IntPtr Name, Description;
        public uint Flags, TxnWaitTimeoutInMSec, ProcessId;
        public IntPtr Sid, UserName;
        public int KernelMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Value0
    {
        public uint Type;
        public ulong Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob
    {
        public uint Size;
        public byte* Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Condition0
    {
        public Guid FieldKey;
        public uint MatchType;
        public Value0 Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Filter0
    {
        public Guid FilterKey;
        public IntPtr DisplayName, DisplayDescription;
        public uint Flags;
        public IntPtr ProviderKey;
        public Blob ProviderData;
        public Guid LayerKey;
        public Guid SubLayerKey;
        public Value0 Weight;
        public uint NumConditions;
        public IntPtr Conditions;
        public uint ActionType;
        public Guid ActionGuid;
        public ulong RawContext, RawContext2;
        public IntPtr Reserved;
        public ulong FilterId;
        public Value0 EffectiveWeight;
    }

    [DllImport("fwpuclnt.dll")]
    private static extern int FwpmEngineOpen0(string? server, uint authn, IntPtr identity, Session0* session, out IntPtr engine);

    [DllImport("fwpuclnt.dll")]
    private static extern int FwpmEngineClose0(IntPtr engine);

    [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode)]
    private static extern int FwpmGetAppIdFromFileName0(string path, out IntPtr appId);

    [DllImport("fwpuclnt.dll")]
    private static extern int FwpmFilterAdd0(IntPtr engine, Filter0* filter, IntPtr sd, out ulong id);

    [DllImport("fwpuclnt.dll")]
    private static extern int FwpmFilterDeleteById0(IntPtr engine, ulong id);

    [DllImport("fwpuclnt.dll")]
    private static extern void FwpmFreeMemory0(IntPtr* memory);
}
