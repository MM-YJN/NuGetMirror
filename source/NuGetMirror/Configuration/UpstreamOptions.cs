using System.ComponentModel.DataAnnotations;

using Microsoft.Extensions.Options;

namespace NuGetMirror.Configuration;

public sealed class UpstreamOptions
{
    /// <summary>
    /// URL of the upstream NuGet service index. Defaults to <c>https://api.nuget.org/v3/index.json</c>.
    /// </summary>
    /// <remarks>
    /// The mirror fetches this document to discover the URLs of all upstream NuGet
    /// resources (flat-container, registrations, search, etc.) and then rewrites those
    /// URLs in proxied responses to point back at the mirror.
    /// </remarks>
    public string IndexUrl { get; set; } = "https://api.nuget.org/v3/index.json";

    /// <summary>
    /// How long the discovered upstream service-index snapshot is cached in memory
    /// before being re-fetched. Defaults to 30 minutes.
    /// </summary>
    /// <remarks>
    /// The discovery cache is shared across all requests. Lowering this value increases
    /// upstream traffic but makes the mirror pick up upstream service-index changes
    /// faster. See also <see cref="BackgroundRefresh"/> and
    /// <see cref="DiscoveryRefreshInterval"/>.
    /// </remarks>
    public TimeSpan DiscoveryCacheTtl { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Optional authentication credentials to include on every upstream request.
    /// </summary>
    /// <remarks>
    /// When <see langword="null"/> (the default) requests are sent without any
    /// <c>Authorization</c> header. Configure this when the upstream feed requires
    /// authentication (e.g., a private Azure Artifacts or GitHub Packages feed).
    /// See <see cref="UpstreamAuthOptions"/> for supported schemes.
    /// </remarks>
    public UpstreamAuthOptions? Auth { get; set; }

    /// <summary>
    /// Optional absolute URL of an HTTP proxy to use for all upstream requests
    /// (e.g. <c>http://proxy.corp.example.com:8080</c>).
    /// </summary>
    /// <remarks>
    /// When <see langword="null"/> or empty (the default) no proxy is used and the
    /// system default proxy settings apply. When set, the value must be a valid
    /// absolute URI.
    /// </remarks>
    public string? Proxy { get; set; }

    /// <summary>
    /// Maximum size in bytes of an upstream response body that may be buffered and
    /// rewritten in memory. Defaults to 67108864 (64 MiB).
    /// </summary>
    /// <remarks>
    /// Responses for endpoints that require URL rewriting (registration,
    /// search, autocomplete, catalog) are fully buffered before being forwarded. If
    /// the upstream response body exceeds this limit, the mirror returns a
    /// <c>502 Bad Gateway</c> error. Increase this value if upstream responses are
    /// legitimately larger than the default. Parsing and rewriting require additional memory.
    /// </remarks>
    [Range(1, long.MaxValue)]
    public long MaxRewriteBodyBytes { get; set; } = 67108864; // 64 MiB

    /// <summary>
    /// Maximum accepted service-index body size, including error responses. Defaults to 4194304 (4 MiB).
    /// Must be positive. Parsing and rewriting require additional memory.
    /// </summary>
    [Range(1, long.MaxValue)]
    public long MaxServiceIndexBodyBytes { get; set; } = 4194304;

    /// <summary>
    /// Configuration for the retry, circuit-breaker, and timeout resilience pipeline
    /// applied to upstream HTTP requests.
    /// </summary>
    /// <remarks>Corresponds to the <c>Mirror:Upstream:Resilience</c> configuration section.</remarks>
    [ValidateObjectMembers]
    public UpstreamResilienceOptions Resilience { get; set; } = new();

    /// <summary>
    /// Additional upstream host base URLs whose absolute URLs should be rewritten to
    /// mirror URLs in proxied service-index responses.
    /// </summary>
    /// <remarks>
    /// The mirror automatically rewrites well-known upstream resource types
    /// (flat-container, registrations, search, etc.). Use this list to add extra
    /// upstream base URLs — for example, additional CDN or regional mirror hosts —
    /// that also appear in the upstream service index and should be redirected through
    /// this mirror. Each entry is treated as a base URL prefix and rewritten to the
    /// mirror's <c>/v3/registration-semver2/</c> prefix.
    /// </remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "Configuration binding requires setter")]
    public List<string> ExtraRewriteHosts { get; set; } = [];

    /// <summary>
    /// Whether the discovery cache is refreshed proactively in the background.
    /// Defaults to <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// When <see langword="true"/>, a background service performs an initial refresh
    /// at startup and then continues to refresh the upstream service-index on a
    /// schedule, keeping the in-memory snapshot warm so that incoming requests are
    /// never blocked waiting for a discovery fetch. The refresh interval is controlled
    /// by <see cref="DiscoveryRefreshInterval"/> (or derived from
    /// <see cref="DiscoveryCacheTtl"/> if not set).
    /// </remarks>
    public bool BackgroundRefresh { get; set; }

    /// <summary>
    /// Override the background discovery refresh interval. Defaults to
    /// <see langword="null"/> (derived automatically).
    /// </summary>
    /// <remarks>
    /// When <see langword="null"/> the interval is <c>DiscoveryCacheTtl − 1 minute</c>
    /// (minimum 1 minute). Set an explicit value to control the refresh cadence
    /// independently of the cache TTL. Requires <see cref="BackgroundRefresh"/> to be
    /// <see langword="true"/>.
    /// </remarks>
    public TimeSpan? DiscoveryRefreshInterval { get; set; }
}
