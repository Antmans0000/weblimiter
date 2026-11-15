using System.Diagnostics;
using Microsoft.Win32;

namespace WebLimiter.App.Util;

/// <summary>
/// Download limit of Windows Delivery Optimization (Windows Update, Microsoft Store, Microsoft 365 Apps when
/// they go through it), set with the documented machine policies DOMax*DownloadBandwidth (KB/s).
/// Works on every engine since Windows itself paces the transfers.
/// </summary>
public static class DeliveryOptimization
{
    private const string Key = @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization";
    private const string Foreground = "DOMaxForegroundDownloadBandwidth";
    private const string Background = "DOMaxBackgroundDownloadBandwidth";

    /// <summary>The limit in KB/s, or null when none is set.</summary>
    public static int? GetLimitKb()
    {
        using var key = Registry.LocalMachine.OpenSubKey(Key);
        return key?.GetValue(Foreground) is int kb && kb > 0 ? kb : null;
    }

    public static void SetLimitKb(int? kb)
    {
        using (var key = Registry.LocalMachine.CreateSubKey(Key, writable: true))
        {
            if (kb is > 0)
            {
                key.SetValue(Foreground, kb.Value, RegistryValueKind.DWord);
                key.SetValue(Background, kb.Value, RegistryValueKind.DWord);
            }
            else
            {
                key.DeleteValue(Foreground, throwOnMissingValue: false);
                key.DeleteValue(Background, throwOnMissingValue: false);
            }
        }
        // The service reads its policies when it starts and is restarted on demand by Windows;
        // stopping it makes a running download pick up the new rate.
        Sc("stop DoSvc");
    }

    private static void Sc(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("sc.exe", args) { CreateNoWindow = true, UseShellExecute = false })!;
            p.WaitForExit(10_000);
        }
        catch { /* the policy still applies at the next start of the service */ }
    }
}
