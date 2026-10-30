using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WebLimiter.App.Util;

public enum ActivityLayout { Standard, SideBySide, ListOnly, Compact }

/// <summary>Interface preferences of the current user (not shared with the service).</summary>
public sealed class UiSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WebLimiter", "ui.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public ActivityLayout ActivityLayout { get; set; } = ActivityLayout.Standard;

    public static UiSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(FilePath), Json) ?? new UiSettings();
        }
        catch { /* corrupt file: defaults */ }
        return new UiSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch { /* preferences are not worth an error */ }
    }
}
