using System.Diagnostics;
using WebLimiter.Core.Backend;
using WebLimiter.Core.Model;

/// <summary>
/// Talks to the installed WebLimiter service through the named pipe: status, snapshot,
/// then blocks and unblocks curl and checks the effect.
/// </summary>
internal static class ServiceTest
{
    public static void Run()
    {
        string curl = Path.Combine(Environment.SystemDirectory, "curl.exe");
        using var backend = new PipeBackend();
        Console.WriteLine($"Connexion au service : {backend.WaitUntilReady(TimeSpan.FromSeconds(10))}");
        Console.WriteLine($"Statut : {backend.GetStatus()}");
        Thread.Sleep(2500);
        var snap = backend.GetSnapshot();
        Console.WriteLine($"Snapshot : {snap.Apps.Count} applis, ↓ {snap.TotalDownRate / 1024:0.0} Ko/s");

        Console.WriteLine($"curl autorisé : code {Curl(curl)}");
        backend.SetRule(new AppRule { AppPath = curl, Blocked = true });
        Console.WriteLine($"Règles côté service : {string.Join(", ", backend.GetRules().Select(r => r.AppPath))}");
        Console.WriteLine($"curl bloqué   : code {Curl(curl)}");
        backend.SetRule(new AppRule { AppPath = curl });
        Console.WriteLine($"curl débloqué : code {Curl(curl)}");

        Thread.Sleep(1500);
        var curlApp = backend.GetSnapshot().Apps.FirstOrDefault(a => a.Path.Equals(curl, StringComparison.OrdinalIgnoreCase));
        Console.WriteLine($"curl vu par le service : {(curlApp == null ? "non" : $"oui, ↓ {curlApp.SessionDown / 1024} Ko")}");
        Console.WriteLine($"Usage du jour (service) : {backend.GetUsageByApp(DateTime.Today, DateTime.Today).Count} applis");
    }

    private static int Curl(string curl)
    {
        using var p = Process.Start(new ProcessStartInfo(curl, "-s -o NUL --max-time 6 https://1.1.1.1/")
            { UseShellExecute = false, CreateNoWindow = true })!;
        p.WaitForExit();
        return p.ExitCode;
    }
}
