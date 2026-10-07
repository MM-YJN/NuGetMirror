using System.ComponentModel.DataAnnotations;

namespace NuGetMirror.Configuration;

public sealed class RegistrationCacheOptions
{
    /// <summary>
    /// Whether registration responses are cached locally. Defaults to <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// When <see langword="false"/>, registration requests are forwarded live to the
    /// upstream server without storing a local copy. When <see langword="true"/> (the
    /// default), responses are stored in the configured cache backend and served from
    /// cache until the <see cref="CacheTtl"/> expires.
    /// Requires <c>Mirror:Cache:Enabled</c> to also be <see langword="true"/>.
    /// </remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How long a cached registration response is considered fresh before it is
    /// re-validated against upstream. Defaults to 30 minutes.
    /// </summary>
    public TimeSpan CacheTtl { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Maximum size in bytes of a registration response body that will be accepted
    /// and cached. Defaults to 67108864 (64 MiB), matching the live rewrite proxy limit.
    /// </summary>
    /// <remarks>
    /// If the upstream response body exceeds this limit, the mirror returns a
    /// <c>502 Bad Gateway</c> error and does not cache the response. Must be ≥ 1.
    /// </remarks>
    [Range(1, long.MaxValue)]
    public long MaxBodyBytes { get; set; } = 67108864; // 64 MiB
}
