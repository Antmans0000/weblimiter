using System;
using System.Collections.Concurrent;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace WebLimiter.App.Util;

/// <summary>Asynchronous, cached reverse DNS lookups (at most a few in flight).</summary>
public static class ReverseDns
{
    private static readonly ConcurrentDictionary<IPAddress, string> Cache = new();
    private static readonly SemaphoreSlim Gate = new(4);

    /// <summary>Returns the cached host name, or null and starts a lookup that calls <paramref name="resolved"/>.</summary>
    public static string? TryGet(IPAddress address, Action<string> resolved)
    {
        if (Cache.TryGetValue(address, out var name)) return name;
        if (!Cache.TryAdd(address, "")) return null;
        _ = Task.Run(async () =>
        {
            await Gate.WaitAsync();
            try
            {
                var entry = await Dns.GetHostEntryAsync(address).WaitAsync(TimeSpan.FromSeconds(5));
                if (!string.IsNullOrEmpty(entry.HostName) && entry.HostName != address.ToString())
                {
                    Cache[address] = entry.HostName;
                    resolved(entry.HostName);
                }
            }
            catch { /* no PTR record */ }
            finally { Gate.Release(); }
        });
        return null;
    }
}
