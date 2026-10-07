using System.Text.Json.Serialization;

namespace NuGetMirror.Diagnostics;

internal sealed class UpstreamStats
{
    [JsonPropertyName("indexUrl")]
    public string IndexUrl { get; init; } = "";

    [JsonPropertyName("discoveredAt")]
    public string? DiscoveredAt { get; init; }

    [JsonPropertyName("resourceCount")]
    public int ResourceCount { get; init; }
}
