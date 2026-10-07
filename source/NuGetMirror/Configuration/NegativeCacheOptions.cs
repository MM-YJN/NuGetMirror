using System.ComponentModel.DataAnnotations;

namespace NuGetMirror.Configuration;

public sealed class NegativeCacheOptions
{
    /// <summary>
    /// Whether 404 (not found) responses from the upstream server are cached in-memory
    /// for flat-container package requests. Defaults to <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// When <see langword="true"/> (the default), a request for a package version that
    /// upstream reported as missing will be served a <c>404 Not Found</c> immediately
    /// from the in-memory cache for the duration of <see cref="Ttl"/>, without making
    /// another upstream request. This reduces upstream traffic when clients repeatedly
    /// probe for non-existent packages (e.g. during dependency resolution).
    /// Requires <c>Mirror:Cache:Enabled</c> to also be <see langword="true"/>.
    /// </remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How long a negative cache entry is retained before it expires.
    /// Defaults to 60 seconds.
    /// </summary>
    /// <remarks>
    /// After this duration the next request for the same missing package will be
    /// forwarded to upstream again and the result re-cached if still absent.
    /// </remarks>
    public TimeSpan Ttl { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Maximum number of entries held in the in-memory negative cache.
    /// Defaults to <c>10000</c>.
    /// </summary>
    /// <remarks>
    /// When the limit is exceeded, the cache evicts expired entries first and then
    /// removes the soonest-to-expire entries until the count drops below half the
    /// configured maximum. The capacity is fixed at startup; changing this value
    /// requires a process restart. Must be ≥ 1.
    /// </remarks>
    [Range(1, int.MaxValue)]
    public int MaxEntries { get; set; } = 10000;
}
