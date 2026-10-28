using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using WebLimiter.Core.Storage;

namespace WebLimiter.Core.Engine;

/// <summary>
/// Upload limits through Windows QoS policies (volatile ActiveStore). Windows has no equivalent
/// for download, so only the send direction is enforced.
/// </summary>
internal sealed class QosLimiter
{
    private const string Prefix = "WebLimiter_";

    private readonly Dictionary<string, long> _applied = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Makes the QoS policies match <paramref name="desired"/> (exe path → bytes/s).</summary>
    public void Apply(IReadOnlyDictionary<string, long> desired)
    {
        var script = new StringBuilder();
        foreach (var (path, rate) in _applied.ToList())
        {
            if (desired.TryGetValue(path, out long wanted) && wanted == rate) continue;
            script.AppendLine($"Remove-NetQosPolicy -Name '{PolicyName(path)}' -PolicyStore ActiveStore -Confirm:$false -ErrorAction SilentlyContinue");
            _applied.Remove(path);
        }
        foreach (var (path, rate) in desired)
        {
            if (_applied.ContainsKey(path)) continue;
            long bits = Math.Max(8000, rate * 8);
            script.AppendLine($"New-NetQosPolicy -Name '{PolicyName(path)}' -AppPathNameMatchCondition '{path.Replace("'", "''")}' " +
                              $"-ThrottleRateActionBitsPerSecond {bits} -PolicyStore ActiveStore | Out-Null");
            _applied[path] = rate;
        }
        if (script.Length > 0) Run(script.ToString());
    }

    public void ClearAll()
    {
        _applied.Clear();
        Run($"Get-NetQosPolicy -PolicyStore ActiveStore -ErrorAction SilentlyContinue | Where-Object {{ $_.Name -like '{Prefix}*' }} | " +
            "Remove-NetQosPolicy -PolicyStore ActiveStore -Confirm:$false -ErrorAction SilentlyContinue");
    }

    private static string PolicyName(string path) =>
        Prefix + Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant())), 0, 6);

    private static void Run(string script)
    {
        try
        {
            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " +
                Convert.ToBase64String(Encoding.Unicode.GetBytes("$ProgressPreference='SilentlyContinue'
" + script)))
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            string err = p.StandardError.ReadToEnd();
            p.WaitForExit(30_000);
            if (p.ExitCode != 0) DataPaths.LogError("Stratégie QoS", new Exception(err));
        }
        catch (Exception ex) { DataPaths.LogError("Stratégie QoS impossible", ex); }
    }
}
