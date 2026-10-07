using System.Text.Json.Serialization;

namespace NuGetMirror.Diagnostics;

internal sealed class MirrorStatsResponse
{
    [JsonPropertyName("version")]
    public string Version { get; init; } = "";

    [JsonPropertyName("uptime")]
    public string Uptime { get; init; } = "";

    [JsonPropertyName("upstream")]
    public UpstreamStats Upstream { get; init; } = new();

    [JsonPropertyName("cache")]
    public CacheStats Cache { get; init; } = new();

    [JsonPropertyName("storage")]
    public StorageStats Storage { get; init; } = new();
}
