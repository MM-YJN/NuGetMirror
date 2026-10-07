using System.ComponentModel.DataAnnotations;

namespace NuGetMirror.Configuration;

public sealed class RepositorySignaturesCacheOptions
{
    /// <summary>
    /// Whether repository-signature index and certificate responses are cached locally.
    /// Defaults to <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// When <see langword="false"/>, repository-signature requests are forwarded live to
    /// the upstream server without storing a local copy. When <see langword="true"/> (the
    /// default), responses are stored in the configured cache backend and served from
    /// cache until the <see cref="CacheTtl"/> expires. Requires both
    /// <c>Mirror:RepositorySignatures:Enabled</c> and <c>Mirror:Cache:Enabled</c> to
    /// also be <see langword="true"/>.
    /// </remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How long a cached repository-signature index or certificate response is considered
    /// fresh before it is re-validated against upstream. Defaults to 24 hours.
    /// </summary>
    public TimeSpan CacheTtl { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Maximum size in bytes of a repository-signature index response body that will be
    /// accepted and cached. Defaults to 4194304 (4 MiB).
    /// </summary>
    /// <remarks>
    /// If the upstream response body exceeds this limit, the mirror returns a
    /// <c>502 Bad Gateway</c> error and does not cache the response. Must be ≥ 1.
    /// </remarks>
    [Range(1, long.MaxValue)]
    public long MaxBodyBytes { get; set; } = 4194304; // 4 MiB
}
