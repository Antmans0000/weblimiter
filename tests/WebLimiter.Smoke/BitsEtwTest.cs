using System.Diagnostics;
using WebLimiter.Core.Engine;
using WebLimiter.Core.Model;
using WebLimiter.Core.Storage;

/// <summary>In-process EtwEngine: BITS transfer allowed, then "svc:BITS" blocked (stops the service meanwhile).</summary>
internal static class BitsEtwTest
{
    public static void Run(string outFile)
    {
        using var log = new StreamWriter(outFile, false) { AutoFlush = true };
        void W(string s) { Console.WriteLine(s); log.WriteLine(s); }
        Sc("stop WebLimiter");
        Thread.Sleep(3000);
        var rules = RuleStore.Load();
        var engine = new EtwEngine(rules, StatsStore.Load());
        try
        {
            engine.Start();
            Thread.Sleep(1500);
            long Bits() => engine.Snapshot.Apps.Where(a => a.Path.Contains("BITS", StringComparison.OrdinalIgnoreCase)).Sum(a => a.SessionDown);

            long b0 = Bits();
            W("Autorisé : " + WuTest.Bits(4));
            Thread.Sleep(2500);
            W($"  BITS reçu : {(Bits() - b0) / 1024} Ko");

            rules.Set(new AppRule { AppPath = "svc:BITS", Blocked = true });
            Thread.Sleep(1500);
            b0 = Bits();
            W("Bloqué : " + WuTest.Bits(4));
            Thread.Sleep(2500);
            W($"  BITS reçu : {(Bits() - b0) / 1024} Ko");
            foreach (var a in engine.Snapshot.Apps.Where(a => a.Path.Contains("BITS", StringComparison.OrdinalIgnoreCase)))
                foreach (var c in engine.GetConnections(a.Path).Take(4))
                    W($"  {c.Protocol} :{c.LocalPort} -> {c.RemoteAddress}:{c.RemotePort} ↓ {c.TotalDown / 1024} Ko");
        }
        catch (Exception ex) { W("ERREUR : " + ex); }
        finally
        {
            rules.Set(new AppRule { AppPath = "svc:BITS" });
            engine.Stop();
            Sc("start WebLimiter");
            W("Terminé, service redémarré.");
        }
    }

    private static void Sc(string args)
    {
        using var p = Process.Start(new ProcessStartInfo("sc.exe", args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        p.WaitForExit(20_000);
    }
}
