using System.Text.Json.Serialization;

namespace NuGetMirror.Json;

internal sealed class ServiceIndex
{
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("resources")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "Configuration binding requires setter")]
    public List<ServiceResource> Resources { get; set; } = [];
}

