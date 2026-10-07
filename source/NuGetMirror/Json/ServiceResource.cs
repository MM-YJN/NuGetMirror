using System.Text.Json.Serialization;

namespace NuGetMirror.Json;

internal sealed class ServiceResource
{
    [JsonPropertyName("@id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("@type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("comment")]
    public string? Comment { get; set; }
}
