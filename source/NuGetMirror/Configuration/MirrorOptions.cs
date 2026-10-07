using Microsoft.Extensions.Options;

namespace NuGetMirror.Configuration;

public sealed class MirrorOptions
{
    public const string SectionName = "Mirror";

    /// <summary>
    /// Configuration for the upstream NuGet server.
    /// </summary>
    /// <remarks>Corresponds to the <c>Mirror:Upstream</c> configuration section.</remarks>
    [ValidateObjectMembers]
    public UpstreamOptions Upstream { get; set; } = new();

    /// <summary>
    /// Configuration for local caching of upstream content.
    /// </summary>
    /// <remarks>Corresponds to the <c>Mirror:Cache</c> configuration section.</remarks>
    [ValidateObjectMembers]
    public CacheOptions Cache { get; set; } = new();

    /// <summary>
    /// Configuration for the admin operational-stats endpoint.
    /// </summary>
    /// <remarks>Corresponds to the <c>Mirror:Admin</c> configuration section.</remarks>
    public AdminOptions Admin { get; set; } = new();

    /// <summary>
    /// Configuration for proxying the upstream RepositorySignatures resource.
    /// </summary>
    /// <remarks>Corresponds to the <c>Mirror:RepositorySignatures</c> configuration section.</remarks>
    public RepositorySignaturesOptions RepositorySignatures { get; set; } = new();

    /// <summary>
    /// The externally-visible base URL of this mirror (e.g. <c>https://nuget.example.com</c>).
    /// </summary>
    /// <remarks>
    /// Used to rewrite upstream URLs in service-index, registration, search, and other
    /// proxied responses so that clients resolve all resources through the mirror.
    /// When <see langword="null"/> or empty, the mirror base URL is derived automatically
    /// from each incoming HTTP request, which is sufficient for most deployments.
    /// Set this explicitly when the mirror sits behind a reverse proxy or load balancer
    /// that changes the host or scheme visible to clients.
    /// </remarks>
    public string? PublicBaseUrl { get; set; }

    /// <summary>
    /// Optional URL path prefix under which all NuGet and admin endpoints are served.
    /// </summary>
    /// <remarks>
    /// When set (e.g. <c>/nuget</c>), every NuGet protocol endpoint and the admin stats
    /// endpoint are registered under that prefix. For example, the service index moves
    /// from <c>/v3/index.json</c> to <c>/nuget/v3/index.json</c>. Health-check endpoints
    /// (<c>/health/live</c>, <c>/health/ready</c>) are not affected and remain at the root.
    /// Rewritten upstream URLs in service-index, registration, and other proxied responses
    /// will also carry the prefix so that clients follow them correctly through the mirror.
    /// <para>
    /// The value may be specified with or without a leading slash and with or without a
    /// trailing slash (e.g. <c>nuget</c>, <c>/nuget</c>, and <c>/nuget/</c> are all
    /// equivalent). Multi-segment prefixes are supported (e.g. <c>/feeds/internal</c>).
    /// </para>
    /// <para>
    /// When combined with <see cref="PublicBaseUrl"/>, the normalized base path is appended
    /// to <see cref="PublicBaseUrl"/> so there is a single source of truth for the subpath.
    /// </para>
    /// <para>
    /// When <see langword="null"/> or empty (the default), endpoints are served at the root,
    /// preserving the existing URL layout.
    /// </para>
    /// </remarks>
    public string? BasePath { get; set; }

    /// <summary>
    /// Returns the canonical (normalized) form of <see cref="BasePath"/>: either an empty
    /// string (no subpath) or a path with a single leading slash and no trailing slash
    /// (e.g. <c>/nuget</c> or <c>/feeds/internal</c>).
    /// </summary>
    /// <remarks>
    /// This property is computed from <see cref="BasePath"/> and is not bound from
    /// configuration.
    /// </remarks>
    public string NormalizedBasePath => NormalizeBasePath(BasePath);

    /// <summary>
    /// Normalizes a raw base-path value to canonical form: empty string (no subpath) or
    /// a string with a single leading slash and no trailing slash.
    /// </summary>
    public static string NormalizeBasePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        // Ensure leading slash, remove trailing slash.
        // Trim whitespace first so that boundary spaces (e.g. " /nuget/ ") don't
        // prevent the subsequent slash-trim from reaching the actual slashes.
        string trimmed = value.Trim().Trim('/');

        return string.IsNullOrEmpty(trimmed) ? string.Empty : "/" + trimmed;
    }
}

