using System.Diagnostics;
using System.Globalization;
using WebLimiter.Core.Engine;
using WebLimiter.Core.Model;
using WebLimiter.Core.Storage;

internal static class EtwTest
{
    private const string Down = "https://speed.cloudflare.com/__down?bytes=30000000";
    private const string Up = "https://speed.cloudflare.com/__up";

    public static void Run(string outFile)
    {
        using var log = new StreamWriter(outFile, false) { AutoFlush = true };
        void W(string s) { Console.WriteLine(s); log.WriteLine(s); }
        string curl = Path.Combine(Environment.SystemDirectory, "curl.exe");
        string payload = Path.Combine(Path.GetTempPath(), "wl-up.bin");
        File.WriteAllBytes(payload, new byte[8_000_000]);

        var rules = RuleStore.Load();
        var engine = new EtwEngine(rules, StatsStore.Load());
        int traced = 0;
        engine.Trace = m => { if (traced++ < 6) W(m); };
        try
        {
            engine.Start();
            W("Moteur ETW démarré.");
            Thread.Sleep(1500);

            var r = Curl(curl, $"-s -o NUL --max-time 8 -w \"%{{speed_download}}\" \"{Down}\"");
            W($"Sans limite   : {r.speed / 1024:0} Ko/s (code {r.exit})");
            Thread.Sleep(1500);
            var app = engine.Snapshot.Apps.FirstOrDefault(a => a.Path.Equals(curl, StringComparison.OrdinalIgnoreCase));
            W($"Compté curl   : ↓ {(app?.SessionDown ?? 0) / 1024} Ko ↑ {(app?.SessionUp ?? 0) / 1024} Ko");
            foreach (var c in engine.GetConnections(curl).Take(3))
                W($"  {c.Protocol} :{c.LocalPort} -> {c.RemoteAddress}:{c.RemotePort} ↓ {c.TotalDown / 1024} Ko ↑ {c.TotalUp / 1024} Ko");

            r = Curl(curl, $"-s -o NUL --max-time 10 --data-binary @{payload} -w \"%{{speed_upload}}\" \"{Up}\"");
            W($"Envoi libre   : {r.speed / 1024:0} Ko/s (code {r.exit})");

            rules.Set(new AppRule { AppPath = curl, UpLimitEnabled = true, UpLimitBytesPerSec = 30 * 1024 });
            Thread.Sleep(6000);
            r = Curl(curl, $"-s -o NUL --max-time 15 --data-binary @{payload} -w \"%{{speed_upload}}\" \"{Up}\"");
            W($"Envoi 30 Ko/s : {r.speed / 1024:0} Ko/s (code {r.exit})");

            rules.Set(new AppRule { AppPath = curl, Blocked = true });
            Thread.Sleep(3000);
            r = Curl(curl, $"-s -o NUL --max-time 5 -w \"%{{speed_download}}\" \"{Down}\"");
            W($"Bloqué        : {r.speed / 1024:0} Ko/s (code {r.exit})");

            rules.Set(new AppRule { AppPath = curl });
            Thread.Sleep(3000);
            r = Curl(curl, $"-s -o NUL --max-time 5 -w \"%{{speed_download}}\" \"{Down}\"");
            W($"Débloqué      : {r.speed / 1024:0} Ko/s (code {r.exit})");

            // blocking an already running transfer
            var p = Process.Start(new ProcessStartInfo(curl, $"-s -o NUL --max-time 14 -w \"%{{speed_download}}\" \"https://speed.cloudflare.com/__down?bytes=2000000000\"")
                { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
            Thread.Sleep(3000);
            long before = engine.Snapshot.Apps.FirstOrDefault(a => a.Path.Equals(curl, StringComparison.OrdinalIgnoreCase))?.SessionDown ?? 0;
            rules.Set(new AppRule { AppPath = curl, Blocked = true });
            Thread.Sleep(4000);
            long mid = engine.Snapshot.Apps.FirstOrDefault(a => a.Path.Equals(curl, StringComparison.OrdinalIgnoreCase))?.SessionDown ?? 0;
            Thread.Sleep(3000);
            long after = engine.Snapshot.Apps.FirstOrDefault(a => a.Path.Equals(curl, StringComparison.OrdinalIgnoreCase))?.SessionDown ?? 0;
            W($"Flux en cours : avant blocage {before / 1024} Ko, +4 s {mid / 1024} Ko, +7 s {after / 1024} Ko");
            p.Kill(); p.WaitForExit();
        }
        catch (Exception ex) { W("ERREUR : " + ex); }
        finally
        {
            rules.Set(new AppRule { AppPath = curl });
            engine.Stop();
            W("Moteur arrêté.");
        }
    }

    private static (double speed, int exit) Curl(string curl, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(curl, args)
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
        string o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        double.TryParse(o.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double s);
        return (s, p.ExitCode);
    }
}
