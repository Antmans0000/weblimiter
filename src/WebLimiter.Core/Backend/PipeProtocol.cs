using System.Text.Json;
using System.Text.Json.Serialization;
using WebLimiter.Core.Model;

namespace WebLimiter.Core.Backend;

/// <summary>One JSON object per line in each direction over the "WebLimiter" named pipe.</summary>
internal static class PipeProtocol
{
    public const string PipeName = "WebLimiter";

    public static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };
}

internal sealed class PipeRequest
{
    public string Op { get; set; } = "";
    public string? Path { get; set; }
    public AppRule? Rule { get; set; }
    public DateTime? From { get; set; }
    public DateTime To { get; set; }
    public bool Enabled { get; set; }
}

internal sealed class PipeResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public JsonElement? Data { get; set; }
}
