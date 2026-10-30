using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace WebLimiter.App.Util;

public sealed record ReleaseInfo(Version Version, string SetupUrl, string SetupName);

/// <summary>Finds the latest WebLimiter release on GitHub and runs its installer.</summary>
public static class Updater
{
    private const string LatestApi = "https://api.github.com/repos/Antmans0000/weblimiter/releases/latest";

    private static readonly HttpClient Http = CreateClient();

    public static Version CurrentVersion { get; } =
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("WebLimiter-Updater");
        return http;
    }

    public static ReleaseInfo GetLatest()
    {
        using var doc = JsonDocument.Parse(Http.GetStringAsync(LatestApi).GetAwaiter().GetResult());
        var root = doc.RootElement;
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version))
            throw new InvalidOperationException($"Version de la dernière publication illisible : « {tag} ».");

        var setup = root.GetProperty("assets").EnumerateArray()
            .Select(a => (Name: a.GetProperty("name").GetString() ?? "", Url: a.GetProperty("browser_download_url").GetString() ?? ""))
            .FirstOrDefault(a => a.Name.StartsWith("WebLimiter-Setup", StringComparison.OrdinalIgnoreCase)
                                 && a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        if (setup.Url is not { Length: > 0 })
            throw new InvalidOperationException($"La version {version.ToString(3)} ne contient pas d'installateur.");
        return new ReleaseInfo(Normalize(version), setup.Url, setup.Name);
    }

    /// <summary>Downloads the installer into the temp folder and returns its path.</summary>
    public static string Download(ReleaseInfo release)
    {
        string path = Path.Combine(Path.GetTempPath(), $"WebLimiter-Setup-{release.Version.ToString(3)}.exe");
        using (var response = Http.GetAsync(release.SetupUrl, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
        {
            response.EnsureSuccessStatusCode();
            using var file = File.Create(path);
            response.Content.CopyToAsync(file).GetAwaiter().GetResult();
        }
        return path;
    }

    /// <summary>Starts the installer, which closes WebLimiter and its service before replacing them.</summary>
    public static void Launch(string setupPath) =>
        Process.Start(new ProcessStartInfo(setupPath) { UseShellExecute = true });

    /// <summary>"0.3" and "0.3.0.0" compare equal to the assembly version "0.3.0.0".</summary>
    private static Version Normalize(Version v) =>
        new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
}
