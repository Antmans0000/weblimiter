using System;
using System.Diagnostics;

namespace WebLimiter.App.Util;

/// <summary>
/// Start-with-Windows through a scheduled task: a Run registry key cannot start an
/// elevated program without a UAC prompt, a task with "highest privileges" can.
/// </summary>
public static class AutoStart
{
    private const string TaskName = "WebLimiter";

    public static bool IsEnabled() => Run($"/Query /TN \"{TaskName}\"") == 0;

    public static bool Set(bool enabled)
    {
        if (!enabled) return Run($"/Delete /TN \"{TaskName}\" /F") == 0;
        string exe = Environment.ProcessPath!;
        return Run($"/Create /TN \"{TaskName}\" /TR \"\\\"{exe}\\\" --tray\" /SC ONLOGON /RL HIGHEST /F") == 0;
    }

    private static int Run(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            p.WaitForExit(10_000);
            return p.ExitCode;
        }
        catch { return -1; }
    }
}
