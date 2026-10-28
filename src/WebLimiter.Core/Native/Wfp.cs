using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace WebLimiter.Core.Native;

/// <summary>
/// Windows Filtering Platform user-mode API: block filters per executable (and per service SID),
/// in a dynamic session so every filter disappears if the process exits.
/// </summary>
internal sealed unsafe class WfpSession : IDisposable
{
    private static readonly Guid[] Layers =
    {
        new("c38d57d1-05a7-4c33-904f-7fbceee60e82"), // ALE_AUTH_CONNECT_V4
        new("4a72393b-319f-44bc-84c3-ba54dcb3b6b4"), // ALE_AUTH_CONNECT_V6
        new("e1cd9fe7-f4b5-4273-96c0-592e487b8650"), // ALE_AUTH_RECV_ACCEPT_V4
        new("a3b42c97-9f04-4672-b87e-cee9c483257f"), // ALE_AUTH_RECV_ACCEPT_V6
    };
    private static readonly Guid ConditionAppId = new("d78e1e87-8644-4ea5-9437-d809ecefc971");
    private static readonly Guid ConditionUserId = new("af043a0a-b34d-4f86-979c-c90371af6e66");

    private IntPtr _engine;

    public WfpSession()
    {
        var session = new Session0 { Flags = 1 }; // FWPM_SESSION_FLAG_DYNAMIC
        int rc = FwpmEngineOpen0(null, 10, IntPtr.Zero, &session, out _engine);
        if (rc != 0) throw new InvalidOperationException($"FwpmEngineOpen0 a échoué (0x{rc:X}).");
    }

    /// <summary>Blocks all traffic of an executable; with a service name, only that service inside a shared host.</summary>
    public List<ulong> Block(string exePath, string? serviceName)
    {
        var ids = new List<ulong>();
        int rc = FwpmGetAppIdFromFileName0(exePath, out IntPtr appId);
        if (rc != 0) throw new InvalidOperationException($"App id introuvable pour {exePath} (0x{rc:X}).");

        IntPtr sd = IntPtr.Zero;
        IntPtr sdBlob = IntPtr.Zero;
        try
        {
            var conditions = stackalloc Condition0[2];
            conditions[0] = new Condition0
            {
                FieldKey = ConditionAppId,
                MatchType = 0,
                Value = new Value0 { Type = 12, Data = (ulong)appId },
            };
            uint count = 1;
            if (serviceName != null)
            {
                sd = ServiceSecurityDescriptor(serviceName, out uint sdLen);
                var blob = (Blob*)NativeMemory.Alloc((nuint)sizeof(Blob));
                blob->Size = sdLen;
                blob->Data = (byte*)sd;
                sdBlob = (IntPtr)blob;
                conditions[1] = new Condition0
                {
                    FieldKey = ConditionUserId,
                    MatchType = 0,
                    Value = new Value0 { Type = 14, Data = (ulong)sdBlob },
                };
                count = 2;
            }

            var name = Marshal.StringToHGlobalUni("WebLimiter");
            try
            {
                foreach (var layer in Layers)
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
            finally { Marshal.FreeHGlobal(name); }
        }
        finally
        {
            FwpmFreeMemory0(&appId);
            if (sdBlob != IntPtr.Zero) NativeMemory.Free((void*)sdBlob);
            if (sd != IntPtr.Zero) LocalFree(sd);
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

    /// <summary>Service SID: S-1-5-80 followed by the five DWORDs of SHA-1 over the upper-cased UTF-16 name.</summary>
    internal static string ServiceSid(string serviceName)
    {
        byte[] hash = SHA1.HashData(Encoding.Unicode.GetBytes(serviceName.ToUpperInvariant()));
        var sb = new StringBuilder("S-1-5-80");
        for (int i = 0; i < 5; i++) sb.Append('-').Append(BitConverter.ToUInt32(hash, i * 4));
        return sb.ToString();
    }

    private static IntPtr ServiceSecurityDescriptor(string serviceName, out uint length)
    {
        string sddl = $"O:LSD:(A;;CC;;;{ServiceSid(serviceName)})";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out IntPtr sd, out length))
            throw new InvalidOperationException($"Descripteur de sécurité invalide ({Marshal.GetLastWin32Error()}).");
        return sd;
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

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision,
        out IntPtr sd, out uint length);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);
}
