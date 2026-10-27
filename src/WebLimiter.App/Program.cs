using System;
using System.Linq;
using WebLimiter.Core.Backend;

namespace WebLimiter.App;

public static class Program
{
    /// <summary>
    /// One executable, two roles: the WPF interface, or with --service the engine host
    /// launched by the service control manager (no UI types are touched in that case).
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--service"))
            return WindowsServiceHost.Run(() => new ServiceRuntime()) ? 0 : 1;

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
