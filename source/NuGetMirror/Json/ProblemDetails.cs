using System.Text.Json.Serialization;

namespace NuGetMirror.Json;

internal sealed record ProblemDetails
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "";

    [JsonPropertyName("title")]
    public string Title { get; init; } = "";

    [JsonPropertyName("status")]
    public int Status { get; init; }

    [JsonPropertyName("detail")]
    public string Detail { get; init; } = "";
}
