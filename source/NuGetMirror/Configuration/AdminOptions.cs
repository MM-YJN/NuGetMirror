namespace NuGetMirror.Configuration;

public sealed class AdminOptions
{
    /// <summary>
    /// Whether the admin operational-stats endpoint is registered. Defaults to <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// When <see langword="true"/>, a <c>GET</c> endpoint is registered at the path
    /// configured in <see cref="Path"/> and returns a JSON document with runtime metrics
    /// such as cache size, upstream request counts, and eviction statistics.
    /// Disable or restrict access to this endpoint in production environments.
    /// </remarks>
    public bool Enabled { get; set; }

    /// <summary>
    /// The URL path at which the admin stats endpoint is served.
    /// Defaults to <c>/admin/stats</c>.
    /// </summary>
    public string Path { get; set; } = "/admin/stats";
}
