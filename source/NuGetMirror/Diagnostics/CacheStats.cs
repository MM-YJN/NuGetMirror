using System.Text.Json.Serialization;

namespace NuGetMirror.Diagnostics;

internal sealed class CacheStats
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("backend")]
    public string Backend { get; init; } = "";

    [JsonPropertyName("evictionEnabled")]
    public bool EvictionEnabled { get; init; }

    [JsonPropertyName("negativeCacheEnabled")]
    public bool NegativeCacheEnabled { get; init; }

    [JsonPropertyName("negativeCacheEntries")]
    public int NegativeCacheEntries { get; init; }

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; init; }

    [JsonPropertyName("entryCount")]
    public long EntryCount { get; init; }

    [JsonPropertyName("lastUpdatedUtc")]
    public string? LastUpdatedUtc { get; init; }
}
