using System.Text.Json;
using System.Text.Json.Serialization;

namespace WebLimiter.Core.Storage;

public static class DataPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WebLimiter");

    public static string Rules => Path.Combine(Root, "rules.json");
    public static string Stats => Path.Combine(Root, "stats.json");
    public static string Log => Path.Combine(Root, "weblimiter.log");

    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Writes atomically (temp file + replace) so a crash never leaves a truncated file.</summary>
    internal static void WriteAtomic(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }

    public static void LogError(string message, Exception? ex = null)
    {
        try
        {
            Directory.CreateDirectory(Root);
            File.AppendAllText(Log, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message} {ex}\r\n");
        }
        catch { /* logging must never throw */ }
    }
}
