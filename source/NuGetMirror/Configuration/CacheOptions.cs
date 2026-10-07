using Microsoft.Extensions.Options;

namespace NuGetMirror.Configuration;

public sealed class CacheOptions
{
    /// <summary>
    /// Whether local caching is enabled.
    /// </summary>
    /// <remarks>
    /// When <see langword="false"/> (the default), all package-content, registration,
    /// search, and other requests are forwarded live to the upstream server without
    /// storing any data locally. Set to <see langword="true"/> to enable caching via
    /// the backend selected by <see cref="Backend"/>.
    /// </remarks>
    public bool Enabled { get; set; }

    /// <summary>
    /// The storage backend to use when caching is enabled.
    /// </summary>
    /// <remarks>
    /// Accepted values are <c>FileSystem</c> (default) and <c>S3</c>.
    /// The <c>FileSystem</c> backend stores cached files under the directory
    /// configured in <see cref="FileSystem"/>. The <c>S3</c> backend stores objects
    /// in an S3-compatible bucket configured in <see cref="S3"/>.
    /// </remarks>
    public string Backend { get; set; } = "FileSystem";

    /// <summary>
    /// Configuration for the <c>FileSystem</c> cache backend.
    /// </summary>
    /// <remarks>Corresponds to the <c>Mirror:Cache:FileSystem</c> configuration section.</remarks>
    [ValidateObjectMembers]
    public FileSystemOptions FileSystem { get; set; } = new();

    /// <summary>
    /// Configuration for the <c>S3</c> cache backend.
    /// </summary>
    /// <remarks>Corresponds to the <c>Mirror:Cache:S3</c> configuration section.</remarks>
    public S3Options S3 { get; set; } = new();

    /// <summary>
    /// Configuration for the periodic cache eviction (clean-up) background service.
    /// </summary>
    /// <remarks>Corresponds to the <c>Mirror:Cache:Eviction</c> configuration section.</remarks>
    [ValidateObjectMembers]
    public CacheEvictionOptions Eviction { get; set; } = new();

    /// <summary>
    /// Configuration for caching upstream vulnerability data.
    /// </summary>
    /// <remarks>Corresponds to the <c>Mirror:Cache:Vulnerability</c> configuration section.</remarks>
    [ValidateObjectMembers]
    public VulnerabilityOptions Vulnerability { get; set; } = new();

    /// <summary>
    /// Configuration for caching upstream package readme files.
    /// </summary>
    /// <remarks>Corresponds to the <c>Mirror:Cache:Readme</c> configuration section.</remarks>
    public ReadmeOptions Readme { get; set; } = new();

    /// <summary>
    /// Configuration for caching upstream repository-signature data.
    /// </summary>
    /// <remarks>Corresponds to the <c>Mirror:Cache:RepositorySignatures</c> configuration section.</remarks>
    [ValidateObjectMembers]
    public RepositorySignaturesCacheOptions RepositorySignatures { get; set; } = new();

    /// <summary>
    /// Configuration for caching upstream registration responses.
    /// </summary>
    /// <remarks>Corresponds to the <c>Mirror:Cache:Registration</c> configuration section.</remarks>
    [ValidateObjectMembers]
    public RegistrationCacheOptions Registration { get; set; } = new();

    /// <summary>
    /// Configuration for the in-memory negative cache that suppresses repeated upstream
    /// requests for packages known to be absent.
    /// </summary>
    /// <remarks>Corresponds to the <c>Mirror:Cache:NegativeCache</c> configuration section.</remarks>
    [ValidateObjectMembers]
    public NegativeCacheOptions NegativeCache { get; set; } = new();

    /// <summary>
    /// Configuration for periodic cache-size metric reporting when eviction is disabled.
    /// </summary>
    /// <remarks>Corresponds to the <c>Mirror:Cache:SizeReporting</c> configuration section.</remarks>
    public CacheSizeReportingOptions SizeReporting { get; set; } = new();
}
