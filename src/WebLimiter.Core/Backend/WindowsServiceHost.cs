using System.Runtime.InteropServices;
using WebLimiter.Core.Storage;

namespace WebLimiter.Core.Backend;

/// <summary>
/// Minimal Windows service plumbing (StartServiceCtrlDispatcher / SetServiceStatus) so the
/// engine can run as the "WebLimiter" service without extra packages.
/// </summary>
public static class WindowsServiceHost
{
    public const string ServiceName = "WebLimiter";

    private const uint ServiceWin32OwnProcess = 0x10;
    private const uint Stopped = 1, StartPending = 2, StopPending = 3, Running = 4;
    private const uint AcceptStop = 0x1, AcceptShutdown = 0x4;
    private const uint ControlStop = 1, ControlInterrogate = 4, ControlShutdown = 5;
    private const uint ErrorCallNotImplemented = 120;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ServiceTableEntry
    {
        public string? Name;
        public IntPtr Proc;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode,
            ServiceSpecificExitCode, CheckPoint, WaitHint;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void ServiceMainProc(uint argc, IntPtr argv);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint HandlerExProc(uint control, uint eventType, IntPtr eventData, IntPtr context);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool StartServiceCtrlDispatcher([In] ServiceTableEntry[] table);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr RegisterServiceCtrlHandlerEx(string name, HandlerExProc handler, IntPtr context);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetServiceStatus(IntPtr handle, ref ServiceStatus status);

    // Kept in static fields so the GC never collects delegates the SCM still calls.
    private static ServiceMainProc? _main;
    private static HandlerExProc? _handler;
    private static IntPtr _statusHandle;
    private static uint _checkPoint;
    private static readonly ManualResetEventSlim StopRequested = new();
    private static Func<IDisposable>? _start;

    /// <summary>
    /// Runs the service until the SCM stops it. Returns false if the process was not started
    /// by the service control manager.
    /// </summary>
    public static bool Run(Func<IDisposable> start)
    {
        _start = start;
        _main = ServiceMain;
        _handler = Handler;
        var table = new[]
        {
            new ServiceTableEntry { Name = ServiceName, Proc = Marshal.GetFunctionPointerForDelegate(_main) },
            new ServiceTableEntry(),
        };
        return StartServiceCtrlDispatcher(table);
    }

    private static void ServiceMain(uint argc, IntPtr argv)
    {
        _statusHandle = RegisterServiceCtrlHandlerEx(ServiceName, _handler!, IntPtr.Zero);
        Report(StartPending, 15000);
        IDisposable? host = null;
        try
        {
            host = _start!();
            Report(Running);
            StopRequested.Wait();
        }
        catch (Exception ex)
        {
            DataPaths.LogError("Erreur fatale du service", ex);
        }
        Report(StopPending, 15000);
        try { host?.Dispose(); }
        catch (Exception ex) { DataPaths.LogError("Arrêt du service", ex); }
        Report(Stopped);
    }

    private static uint Handler(uint control, uint eventType, IntPtr eventData, IntPtr context)
    {
        switch (control)
        {
            case ControlStop:
            case ControlShutdown:
                Report(StopPending, 15000);
                StopRequested.Set();
                return 0;
            case ControlInterrogate:
                return 0;
            default:
                return ErrorCallNotImplemented;
        }
    }

    private static void Report(uint state, uint waitHint = 0)
    {
        var status = new ServiceStatus
        {
            ServiceType = ServiceWin32OwnProcess,
            CurrentState = state,
            ControlsAccepted = state == Running ? AcceptStop | AcceptShutdown : 0,
            WaitHint = waitHint,
            CheckPoint = state is Running or Stopped ? 0 : ++_checkPoint,
        };
        SetServiceStatus(_statusHandle, ref status);
    }
}

/// <summary>What the service process hosts: the engine plus the pipe server in front of it.</summary>
public sealed class ServiceRuntime : IDisposable
{
    private readonly LocalBackend _backend;
    private readonly PipeServer _server;

    public ServiceRuntime()
    {
        _backend = new LocalBackend(isService: true);
        _backend.SetFiltering(true);
        _server = new PipeServer(_backend);
    }

    public void Dispose()
    {
        _server.Dispose();
        _backend.Dispose();
    }
}
