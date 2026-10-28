// End-to-end smoke test of the engine (must run elevated):
// measures curl's download speed unlimited, limited to 200 KB/s, then blocked.
using System.Diagnostics;
using System.Globalization;
using WebLimiter.Core.Engine;
using WebLimiter.Core.Model;
using WebLimiter.Core.Storage;

if (args.Length > 1 && args[0] == "wu") { WuTest.Run(args[1], args.Skip(2).ToArray()); return; }
if (args.Length > 1 && args[0] == "etw") { EtwTest.Run(args[1]); return; }
if (args.Length > 0 && args[0] == "bits") { BitsTest.Run(); return; }
if (args.Length > 0 && args[0] == "service") { ServiceTest.Run(); return; }
if (args.Length > 2 && args[0] == "ui") { UiServiceTest.Run(args[1], args[2]); return; }

const string Url = "https://speed.cloudflare.com/__down?bytes=50000000";
string curl = Path.Combine(Environment.SystemDirectory, "curl.exe");

var rules = RuleStore.Load();
var engine = new TrafficEngine(rules, StatsStore.Load());
var curlPorts = new System.Collections.Concurrent.ConcurrentQueue<string>();
engine.Trace = m => { if (m.Contains("rport=443")) curlPorts.Enqueue($"{DateTime.Now:HH:mm:ss.fff} {m}"); };
engine.Start();
Console.WriteLine("Moteur démarré.");
Thread.Sleep(1500);

try
{
    Report("Sans limite", Download(6));
    Report("Sans limite", Download(6));

    foreach (int kb in new[] { 1000, 500, 200 })
    {
        rules.Set(new AppRule { AppPath = curl, DownLimitEnabled = true, DownLimitBytesPerSec = kb * 1024 });
        Report($"Limite {kb} Ko/s", Download(12));
    }

    rules.Set(new AppRule { AppPath = curl, Blocked = true });
    Report("Bloqué", Download(5));

    var snap = engine.Snapshot;
    Console.WriteLine($"\nSnapshot : {snap.Apps.Count} applis, ↓ {snap.TotalDownRate / 1024:0} Ko/s");
    foreach (var a in snap.Apps.OrderByDescending(a => a.SessionDown + a.SessionUp).Take(8))
        Console.WriteLine($"  {a.Name,-35} ↓ {a.SessionDown / 1024,8} Ko  ↑ {a.SessionUp / 1024,8} Ko  conn {a.Connections}");
    foreach (var c in engine.GetConnections("System").Take(6))
        Console.WriteLine($"  [System] {c.Protocol} :{c.LocalPort} -> {c.RemoteAddress}:{c.RemotePort}  ↓ {c.TotalDown / 1024} Ko");
    var curlConns = engine.GetConnections(curl);
    Console.WriteLine($"Connexions curl suivies : {curlConns.Count}");
    foreach (var c in curlConns.Take(3))
        Console.WriteLine($"  {c.Protocol} :{c.LocalPort} -> {c.RemoteAddress}:{c.RemotePort}  ↓ {c.TotalDown / 1024} Ko");
}
finally
{
    rules.Set(new AppRule { AppPath = curl }); // default rule == removed
    engine.Stop();
    Console.WriteLine("Moteur arrêté, règle de test supprimée.");
}

(double speed, int exit) Download(int seconds)
{
    var psi = new ProcessStartInfo(curl, $"-s -o NUL --max-time {seconds} -w \"%{{speed_download}}\" \"{Url}\"")
    {
        RedirectStandardOutput = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };
    using var p = Process.Start(psi)!;
    string output = p.StandardOutput.ReadToEnd();
    p.WaitForExit();
    double.TryParse(output.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double speed);
    return (speed, p.ExitCode);
}

static void Report(string label, (double speed, int exit) r) =>
    Console.WriteLine($"{label,-18} : {r.speed / 1024,8:0} Ko/s   (code curl {r.exit})");
