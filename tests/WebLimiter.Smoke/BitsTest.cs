using System.Diagnostics;
using WebLimiter.Core.Engine;
using WebLimiter.Core.Model;
using WebLimiter.Core.Storage;

/// <summary>
/// Downloads through the BITS service (bitsadmin), first allowed then with the "svc:BITS"
/// rule blocked, and reports what the engine attributed to the service.
/// </summary>
internal static class BitsTest
{
    // BITS needs a server that announces the size and supports ranges.
    private const string Url = "https://proof.ovh.net/files/100Mb.dat";
    private const string Job = "WebLimiterSmoke";

    public static void Run()
    {
        var rules = RuleStore.Load();
        var engine = new TrafficEngine(rules, StatsStore.Load());
        var trace = new System.Collections.Concurrent.ConcurrentQueue<string>();
        engine.Trace = m => trace.Enqueue($"{DateTime.Now:HH:mm:ss.fff} {m}");
        engine.Start();
        Console.WriteLine("Moteur démarré.");
        var bits = ServiceCatalog.Get("BITS");
        Console.WriteLine($"Catalogue : {ServiceCatalog.GetAll().Count} services");
        Console.WriteLine("Services Windows Update détectés : " + string.Join(", ", ServiceCatalog.GetWindowsUpdateServices().Select(s => $"{s.Name} ({s.DisplayName})")));
        string key = bits?.RuleKey ?? "svc:BITS";
        Console.WriteLine($"Clé de règle BITS : {key}");
        try
        {
            Console.WriteLine($"Autorisé : {Measure(engine, key, 12) / 1024} Ko reçus par BITS");
            rules.Set(new AppRule { AppPath = key, Blocked = true });
            Console.WriteLine($"Bloqué   : {Measure(engine, key, 12) / 1024} Ko reçus par BITS");

            var unknown = engine.GetConnections("?").Where(c => c.TotalDown > 100_000).ToList();
            foreach (var c in unknown)
            {
                Console.WriteLine($"  [?] {c.Protocol} :{c.LocalPort} -> {c.RemoteAddress}:{c.RemotePort} ↓ {c.TotalDown / 1024} Ko");
                foreach (var t in trace.Where(t => t.Contains($"lport={c.LocalPort} "))) Console.WriteLine("       " + t);
            }
            Console.WriteLine($"  ({trace.Count} événements socket au total, {trace.Count(t => t.Contains("pid=11156"))} pour BITS)");
            Console.WriteLine("Applis vues par le moteur :");
            foreach (var a in engine.Snapshot.Apps.Take(15))
                Console.WriteLine($"  {a.Path,-28} {a.Name,-55} ↓ {a.SessionDown / 1024} Ko bloqué={a.Blocked}");
        }
        finally
        {
            rules.Set(new AppRule { AppPath = key });
            engine.Stop();
            Console.WriteLine("Moteur arrêté, règle de test supprimée.");
        }
    }

    private static long Measure(TrafficEngine engine, string key, int seconds)
    {
        long before = engine.Snapshot.Apps.FirstOrDefault(a => a.Path == key)?.SessionDown ?? 0;
        string target = Path.Combine(Path.GetTempPath(), "weblimiter-bits.tmp");
        var p = Process.Start(new ProcessStartInfo("bitsadmin.exe",
            $"/transfer {Job} /download /priority FOREGROUND \"{Url}\" \"{target}\"")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
        })!;
        p.WaitForExit(seconds * 1000);
        if (p.HasExited) Console.WriteLine("  bitsadmin: " + p.StandardOutput.ReadToEnd().Trim().Split('\n').LastOrDefault());
        Thread.Sleep(1500); // let the engine tick
        Run("bitsadmin.exe", $"/cancel {Job}");
        if (!p.HasExited) p.Kill();
        File.Delete(target);
        long after = engine.Snapshot.Apps.FirstOrDefault(a => a.Path == key)?.SessionDown ?? 0;
        return after - before;
    }

    private static void Run(string exe, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, args)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        p.WaitForExit(10_000);
    }
}
