using System.Runtime.InteropServices;

namespace WebLimiter.Core.Native;

/// <summary>Real-time ETW session consuming Microsoft-Windows-Kernel-Network (no kernel driver).</summary>
internal sealed unsafe class EtwSession : IDisposable
{
    public delegate void EventHandler(ushort id, byte* data, int length);

    private static readonly Guid KernelNetwork = new("7dd42a49-5329-4832-8dfd-43d979153a88");
    private const string SessionName = "WebLimiter-Network";
    private const int PropsSize = 120;
    private const int NameBytes = 2 * 64;
    private const ulong InvalidTraceHandle = ulong.MaxValue;

    private readonly EventHandler _handler;
    private readonly EventRecordCallback _callback;
    private ulong _session;
    private ulong _consumer = InvalidTraceHandle;
    private Thread? _thread;

    public EtwSession(EventHandler handler)
    {
        _handler = handler;
        _callback = OnEvent;
    }

    public void Start()
    {
        StopStale();
        byte* props = NewProperties();
        try
        {
            *(uint*)(props + 64) = 0x100;                 // LogFileMode: REAL_TIME
            *(uint*)(props + 68) = 1;                     // FlushTimer (s)
            int rc = StartTraceW(out _session, SessionName, props);
            if (rc != 0) throw new InvalidOperationException($"StartTrace a échoué (erreur Windows {rc}).");
        }
        finally { NativeMemory.Free(props); }

        var provider = KernelNetwork;
        int er = EnableTraceEx2(_session, ref provider, 1, 5, ulong.MaxValue, 0, 0, IntPtr.Zero);
        if (er != 0)
        {
            Dispose();
            throw new InvalidOperationException($"EnableTraceEx2 a échoué (erreur Windows {er}).");
        }

        var name = Marshal.StringToHGlobalUni(SessionName);
        try
        {
            var log = new EventTraceLogfile
            {
                LoggerName = name,
                ProcessTraceMode = 0x100 | 0x10000000,    // REAL_TIME | EVENT_RECORD
                EventRecordCallback = Marshal.GetFunctionPointerForDelegate(_callback),
            };
            _consumer = OpenTraceW(ref log);
            if (_consumer == InvalidTraceHandle)
            {
                int oe = Marshal.GetLastWin32Error();
                Dispose();
                throw new InvalidOperationException($"OpenTrace a échoué (erreur Windows {oe}).");
            }
        }
        finally { Marshal.FreeHGlobal(name); }

        _thread = new Thread(() =>
        {
            ulong h = _consumer;
            ProcessTrace(&h, 1, IntPtr.Zero, IntPtr.Zero);
        }) { IsBackground = true, Name = "WebLimiter.Etw" };
        _thread.Start();
    }

    private static byte* NewProperties()
    {
        int total = PropsSize + NameBytes;
        byte* p = (byte*)NativeMemory.AllocZeroed((nuint)total);
        *(uint*)p = (uint)total;                          // Wnode.BufferSize
        *(uint*)(p + 44) = 0x20000;                       // Wnode.Flags: TRACED_GUID
        *(uint*)(p + 40) = 1;                             // Wnode.ClientContext: QPC
        *(uint*)(p + 112) = 0;                            // LogFileNameOffset
        *(uint*)(p + 116) = PropsSize;                    // LoggerNameOffset
        return p;
    }

    private static void StopStale()
    {
        byte* props = NewProperties();
        try { ControlTraceW(0, SessionName, props, 1); }
        finally { NativeMemory.Free(props); }
    }

    private void OnEvent(byte* record)
    {
        try
        {
            ushort id = *(ushort*)(record + 40);
            ushort length = *(ushort*)(record + 86);
            byte* data = *(byte**)(record + 96);
            if (data != null) _handler(id, data, length);
        }
        catch { /* never let an exception cross into ETW */ }
    }

    public void Dispose()
    {
        if (_session != 0)
        {
            byte* props = NewProperties();
            try { ControlTraceW(_session, null, props, 1); }
            finally { NativeMemory.Free(props); }
            _session = 0;
        }
        if (_consumer != InvalidTraceHandle)
        {
            CloseTrace(_consumer);
            _consumer = InvalidTraceHandle;
        }
        _thread?.Join(2000);
        _thread = null;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void EventRecordCallback(byte* record);

    [StructLayout(LayoutKind.Sequential)]
    private struct TimeZoneInformation
    {
        public int Bias;
        public fixed char StandardName[32];
        public fixed ushort StandardDate[8];
        public int StandardBias;
        public fixed char DaylightName[32];
        public fixed ushort DaylightDate[8];
        public int DaylightBias;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventTrace
    {
        public fixed byte Header[48];
        public uint InstanceId, ParentInstanceId;
        public Guid ParentGuid;
        public IntPtr MofData;
        public uint MofLength, ClientContext;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TraceLogfileHeader
    {
        public uint BufferSize, Version, ProviderVersion, NumberOfProcessors;
        public long EndTime;
        public uint TimerResolution, MaximumFileSize, LogFileMode, BuffersWritten;
        public Guid LogInstanceGuid;
        public IntPtr LoggerName, LogFileName;
        public TimeZoneInformation TimeZone;
        public long BootTime, PerfFreq, StartTime;
        public uint ReservedFlags, BuffersLost;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventTraceLogfile
    {
        public IntPtr LogFileName;
        public IntPtr LoggerName;
        public long CurrentTime;
        public uint BuffersRead;
        public uint ProcessTraceMode;
        public EventTrace CurrentEvent;
        public TraceLogfileHeader LogfileHeader;
        public IntPtr BufferCallback;
        public uint BufferSize, Filled, EventsLost;
        public IntPtr EventRecordCallback;
        public uint IsKernelTrace;
        public IntPtr Context;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int StartTraceW(out ulong handle, string name, byte* properties);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int ControlTraceW(ulong handle, string? name, byte* properties, uint code);

    [DllImport("advapi32.dll")]
    private static extern int EnableTraceEx2(ulong handle, ref Guid provider, uint control, byte level,
        ulong matchAny, ulong matchAll, uint timeout, IntPtr parameters);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern ulong OpenTraceW(ref EventTraceLogfile logfile);

    [DllImport("advapi32.dll")]
    private static extern int ProcessTrace(ulong* handles, uint count, IntPtr start, IntPtr end);

    [DllImport("advapi32.dll")]
    private static extern int CloseTrace(ulong handle);
}
