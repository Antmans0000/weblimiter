using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using WebLimiter.Core.Engine;
using WebLimiter.Core.Model;
using WebLimiter.Core.Storage;

namespace WebLimiter.Core.Backend;

/// <summary>Client side of <see cref="PipeServer"/>: talks to the WebLimiter Windows service.</summary>
public sealed class PipeBackend : IBackend
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(3);
    private const int ConnectTimeoutMs = 300;
    private const long RetryDelayMs = 3000;

    private readonly object _lock = new();
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private long _lastAttempt = -RetryDelayMs;

    /// <summary>Waits until the service answers (after it was just started).</summary>
    public bool WaitUntilReady(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            lock (_lock)
            {
                _lastAttempt = -RetryDelayMs;
                if (TryConnect()) return true;
            }
            Thread.Sleep(250);
        }
        return false;
    }

    public BackendStatus GetStatus()
    {
        try { return Call<BackendStatus>(new PipeRequest { Op = "Status" }) ?? Disconnected(); }
        catch { return Disconnected(); }
    }

    private static BackendStatus Disconnected() => new(false, false, null, true);

    public EngineSnapshot GetSnapshot() => Call<EngineSnapshot>(new PipeRequest { Op = "Snapshot" }) ?? EngineSnapshot.Empty;

    public IReadOnlyList<ConnectionSnapshot> GetConnections(string appPath) =>
        Call<List<ConnectionSnapshot>>(new PipeRequest { Op = "Connections", Path = appPath }) ?? new();

    public IReadOnlyList<AppRule> GetRules() => Call<List<AppRule>>(new PipeRequest { Op = "Rules" }) ?? new();

    public void SetRule(AppRule rule) => Call<object>(new PipeRequest { Op = "SetRule", Rule = rule });

    public IReadOnlyList<AppUsage> GetUsageByApp(DateTime? from, DateTime to) =>
        Call<List<AppUsage>>(new PipeRequest { Op = "Usage", From = from, To = to }) ?? new();

    public IReadOnlyList<DayUsage> GetDaily(DateTime from, DateTime to) =>
        Call<List<DayUsage>>(new PipeRequest { Op = "Daily", From = from, To = to }) ?? new();

    public void ClearStats() => Call<object>(new PipeRequest { Op = "ClearStats" });

    public void SetFiltering(bool enabled) => Call<object>(new PipeRequest { Op = "SetFiltering", Enabled = enabled });

    private T? Call<T>(PipeRequest request)
    {
        lock (_lock)
        {
            if (_pipe is not { IsConnected: true } && !TryConnect())
                throw new IOException("Le service WebLimiter ne répond pas.");
            try
            {
                _writer!.WriteLine(JsonSerializer.Serialize(request, PipeProtocol.Json));
                var read = _reader!.ReadLineAsync();
                if (!read.Wait(CallTimeout)) throw new TimeoutException("Le service WebLimiter ne répond pas.");
                string line = read.Result ?? throw new IOException("Connexion au service fermée.");
                var response = JsonSerializer.Deserialize<PipeResponse>(line, PipeProtocol.Json)!;
                if (!response.Ok) throw new InvalidOperationException(response.Error);
                return response.Data is { ValueKind: not JsonValueKind.Null } data
                    ? data.Deserialize<T>(PipeProtocol.Json)
                    : default;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or ObjectDisposedException or AggregateException)
            {
                Disconnect();
                throw new IOException("Le service WebLimiter ne répond pas.", ex);
            }
        }
    }

    private bool TryConnect()
    {
        // Avoid freezing the UI every second while the service is down.
        if (Environment.TickCount64 - _lastAttempt < RetryDelayMs) return false;
        _lastAttempt = Environment.TickCount64;
        Disconnect();
        try
        {
            var pipe = new NamedPipeClientStream(".", PipeProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.Connect(ConnectTimeoutMs);
            _pipe = pipe;
            _reader = new StreamReader(pipe, Encoding.UTF8);
            _writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            if (ex is UnauthorizedAccessException) DataPaths.LogError("Accès au service refusé (droits administrateur requis)");
            return false;
        }
    }

    private void Disconnect()
    {
        _reader?.Dispose();
        _writer = null;
        _reader = null;
        try { _pipe?.Dispose(); } catch { /* broken pipe */ }
        _pipe = null;
    }

    public void Dispose()
    {
        lock (_lock) Disconnect();
    }
}

/// <summary>Delegates to a backend that can be swapped at runtime (in-process engine ↔ service).</summary>
public sealed class SwitchableBackend : IBackend
{
    private volatile IBackend _current;

    public SwitchableBackend(IBackend initial) => _current = initial;

    public IBackend Current => _current;

    /// <summary>Replaces the backend; the previous one is disposed.</summary>
    public void Swap(IBackend next)
    {
        var old = _current;
        _current = next;
        if (!ReferenceEquals(old, next)) old.Dispose();
    }

    public BackendStatus GetStatus() => _current.GetStatus();
    public EngineSnapshot GetSnapshot() => _current.GetSnapshot();
    public IReadOnlyList<ConnectionSnapshot> GetConnections(string appPath) => _current.GetConnections(appPath);
    public IReadOnlyList<AppRule> GetRules() => _current.GetRules();
    public void SetRule(AppRule rule) => _current.SetRule(rule);
    public IReadOnlyList<AppUsage> GetUsageByApp(DateTime? from, DateTime to) => _current.GetUsageByApp(from, to);
    public IReadOnlyList<DayUsage> GetDaily(DateTime from, DateTime to) => _current.GetDaily(from, to);
    public void ClearStats() => _current.ClearStats();
    public void SetFiltering(bool enabled) => _current.SetFiltering(enabled);
    public void Dispose() => _current.Dispose();
}
