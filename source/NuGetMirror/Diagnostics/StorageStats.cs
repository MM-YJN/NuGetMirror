using System.Text.Json.Serialization;

namespace NuGetMirror.Diagnostics;

internal sealed class StorageStats
{
    [JsonPropertyName("healthy")]
    public bool? Healthy { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}
