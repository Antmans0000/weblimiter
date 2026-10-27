using System;
using System.Diagnostics;
using System.Threading;
using WebLimiter.Core.Backend;
using WebLimiter.Core.Engine;

namespace WebLimiter.App.Util;

/// <summary>Installs / removes the WebLimiter Windows service with sc.exe.</summary>
public static class ServiceInstaller
{
    private const string Name = WindowsServiceHost.ServiceName;

    public static ServiceInfo? Query() => ServiceCatalog.GetAll(force: true)
        .FirstOrDefaultName(Name);

    public static bool IsInstalled => Query() != null;
    public static bool IsRunning => Query()?.IsRunning == true;

    public static void Install()
    {
        string exe = Environment.ProcessPath!;
        Sc($"create {Name} binPath= \"\\\"{exe}\\\" --service\" start= auto DisplayName= \"WebLimiter\" depend= BFE");
        Sc($"description {Name} \"Surveille, limite et bloque le trafic réseau par application (moteur WebLimiter).\"", check: false);
        // Restart automatically if the engine crashes.
        Sc($"failure {Name} reset= 86400 actions= restart/5000/restart/5000/restart/30000", check: false);
        Start();
    }

    public static void Start()
    {
        if (!IsRunning) Sc($"start {Name}");
        WaitFor(s => s?.IsRunning == true, "démarrer");
    }

    public static void Uninstall()
    {
        if (Query() is { State: not 1 }) Sc($"stop {Name}", check: false);
        WaitFor(s => s == null || s.State == 1, "s'arrêter");
        Sc($"delete {Name}");
    }

    private static void WaitFor(Func<ServiceInfo?, bool> done, string what)
    {
        for (int i = 0; i < 60; i++)
        {
            if (done(Query())) return;
            Thread.Sleep(250);
        }
        throw new InvalidOperationException($"Le service WebLimiter n'a pas pu {what} à temps.");
    }

    private static void Sc(string args, bool check = true)
    {
        using var p = Process.Start(new ProcessStartInfo("sc.exe", args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(15_000);
        if (check && p.ExitCode != 0)
            throw new InvalidOperationException($"sc {args.Split(' ')[0]} a échoué (code {p.ExitCode}) : {output.Trim()}");
    }

    private static ServiceInfo? FirstOrDefaultName(this System.Collections.Generic.IEnumerable<ServiceInfo> list, string name)
    {
        foreach (var s in list)
            if (string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) return s;
        return null;
    }
}
