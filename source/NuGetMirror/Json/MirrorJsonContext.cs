using System.Text.Json.Serialization;

using NuGetMirror.Diagnostics;

namespace NuGetMirror.Json;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = false, WriteIndented = false)]
[JsonSerializable(typeof(ServiceIndex))]
[JsonSerializable(typeof(ProblemDetails))]
[JsonSerializable(typeof(List<VulnerabilityIndexEntry>))]
[JsonSerializable(typeof(MirrorStatsResponse))]
internal sealed partial class MirrorJsonContext : JsonSerializerContext
{
}
