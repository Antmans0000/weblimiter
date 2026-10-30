using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using WebLimiter.Core.Storage;

namespace WebLimiter.Core.Backend;

/// <summary>
/// Serves an <see cref="IBackend"/> over a named pipe. Only SYSTEM and elevated
/// administrators may connect, since a client can block or throttle any program.
/// </summary>
public sealed class PipeServer : IDisposable
{
    private const int MaxClients = 8;

    private readonly IBackend _backend;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _listeners = new();

    public PipeServer(IBackend backend)
    {
        _backend = backend;
        for (int i = 0; i < 2; i++) _listeners.Add(Task.Run(ListenLoop));
    }

    private static NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(PipeProtocol.PipeName, PipeDirection.InOut, MaxClients,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
    }

    private async Task ListenLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(_cts.Token);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                DataPaths.LogError("Canal nommé indisponible", ex);
                await Task.Delay(1000);
                continue;
            }
            _ = Task.Run(() => Serve(pipe));
        }
    }

    private async Task Serve(NamedPipeServerStream pipe)
    {
        using (pipe)
        using (var reader = new StreamReader(pipe, Encoding.UTF8))
        using (var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true })
        {
            try
            {
                while (!_cts.IsCancellationRequested && pipe.IsConnected)
                {
                    string? line = await reader.ReadLineAsync(_cts.Token);
                    if (line == null) return;
                    await writer.WriteLineAsync(Handle(line));
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // client went away
            }
        }
    }

    private string Handle(string line)
    {
        var response = new PipeResponse();
        try
        {
            var req = JsonSerializer.Deserialize<PipeRequest>(line, PipeProtocol.Json)
                      ?? throw new InvalidDataException("Requête vide");
            object? data = req.Op switch
            {
                "Status" => _backend.GetStatus(),
                "Snapshot" => _backend.GetSnapshot(),
                "Connections" => _backend.GetConnections(req.Path ?? ""),
                "Rules" => _backend.GetRules(),
                "SetRule" => Do(() => _backend.SetRule(req.Rule ?? throw new InvalidDataException("Règle manquante"))),
                "Usage" => _backend.GetUsageByApp(req.From, req.To),
                "TotalUsage" => _backend.GetTotalUsage(req.From, req.To),
                "Daily" => _backend.GetDaily(req.From ?? req.To, req.To),
                "ClearStats" => Do(_backend.ClearStats),
                "SetFiltering" => Do(() => _backend.SetFiltering(req.Enabled)),
                _ => throw new InvalidDataException($"Opération inconnue : {req.Op}"),
            };
            response.Ok = true;
            response.Data = JsonSerializer.SerializeToElement(data, PipeProtocol.Json);
        }
        catch (Exception ex)
        {
            response.Ok = false;
            response.Error = ex.Message;
        }
        return JsonSerializer.Serialize(response, PipeProtocol.Json);
    }

    private static object? Do(Action action)
    {
        action();
        return null;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { Task.WaitAll(_listeners.ToArray(), 2000); } catch { /* cancelled */ }
    }
}
