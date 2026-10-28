using System.Diagnostics;
using WebLimiter.Core.Backend;
using WebLimiter.Core.Engine;
using WebLimiter.Core.Model;

/// <summary>Through the installed service: BITS transfer and a Windows Update scan, allowed then blocked.</summary>
internal static class WuTest
{
    private const string Url = "https://proof.ovh.net/files/100Mb.dat";
    private const string Job = "WebLimiterWu";

    public static void Run(string outFile, params string[] args)
    {
        using var log = new StreamWriter(outFile, false) { AutoFlush = true };
        void W(string s) { Console.WriteLine(s); log.WriteLine(s); }

        using var backend = new PipeBackend();
        W($"Service joignable : {backend.WaitUntilReady(TimeSpan.FromSeconds(10))}  {backend.GetStatus()}");
        var wu = ServiceCatalog.GetWindowsUpdateServices();
        var keys = wu.Select(s => s.RuleKey).ToList();
        W("Services MAJ : " + string.Join(", ", wu.Select(s => s.Name)));

        long Down(string[] names) => backend.GetSnapshot().Apps
            .Where(a => a.Path.StartsWith("svc:", StringComparison.OrdinalIgnoreCase)
                        && names.Any(n => a.Path.Contains(n, StringComparison.OrdinalIgnoreCase)))
            .Sum(a => a.SessionDown);

        void Dump(Dictionary<string, long> before)
        {
            foreach (var a in backend.GetSnapshot().Apps)
            {
                long delta = a.SessionDown - (before.TryGetValue(a.Path, out long v) ? v : 0);
                if (delta > 30_000) W($"  {a.Path,-40} +{delta / 1024} Ko  bloqué={a.Blocked}");
            }
        }

        try
        {
            W("--- BITS autorisé ---");
            var before = backend.GetSnapshot().Apps.ToDictionary(x => x.Path, x => x.SessionDown);
            W(Bits(6));
            Thread.Sleep(2500);
            Dump(before);

            backend.SetRule(new AppRule { AppPath = "svc:BITS", Blocked = true });
            Thread.Sleep(3000);
            W("--- BITS bloqué ---");
            before = backend.GetSnapshot().Apps.ToDictionary(x => x.Path, x => x.SessionDown);
            W(Bits(6));
            Thread.Sleep(2500);
            Dump(before);
            backend.SetRule(new AppRule { AppPath = "svc:BITS" });
            Thread.Sleep(2000);
            if (args.Length > 0) return;

            long d0;
            var names = wu.Select(s => s.Name).ToArray();
            W("--- Scan Windows Update autorisé ---");
            d0 = Down(names);
            Scan();
            Thread.Sleep(25000);
            W($"Services MAJ : +{(Down(names) - d0) / 1024} Ko");

            foreach (var k in keys) backend.SetRule(new AppRule { AppPath = k, Blocked = true });
            Thread.Sleep(3000);
            W("--- Scan Windows Update bloqué ---");
            d0 = Down(names);
            Scan();
            Thread.Sleep(25000);
            W($"Services MAJ : +{(Down(names) - d0) / 1024} Ko");
        }
        catch (Exception ex) { W("ERREUR : " + ex); }
        finally
        {
            foreach (var k in keys.Append("svc:BITS")) backend.SetRule(new AppRule { AppPath = k });
            W("Règles de test supprimées.");
        }
    }

    private static void DumpStub() { }

    private static void Scan()
    {
        string uso = Path.Combine(Environment.SystemDirectory, "UsoClient.exe");
        using var p = Process.Start(new ProcessStartInfo(uso, "StartScan") { UseShellExecute = false, CreateNoWindow = true })!;
        p.WaitForExit(10_000);
    }

    internal static string Bits(int seconds)
    {
        string target = Path.Combine(Path.GetTempPath(), "weblimiter-wu.tmp");
        var p = Process.Start(new ProcessStartInfo("bitsadmin.exe",
            $"/transfer {Job} /download /priority FOREGROUND \"{Url}\" \"{target}\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        bool done = p.WaitForExit(seconds * 1000);
        string outp = done ? string.Join(" | ", p.StandardOutput.ReadToEnd().Split((char)10, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)) : "(coupé après " + seconds + " s)";
        Thread.Sleep(500);
        using (var c = Process.Start(new ProcessStartInfo("bitsadmin.exe", $"/cancel {Job}")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!) c.WaitForExit(10_000);
        if (!p.HasExited) p.Kill();
        File.Delete(target);
        return "bitsadmin: " + outp;
    }
}
