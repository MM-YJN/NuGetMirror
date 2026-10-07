namespace NuGetMirror.Configuration;

public sealed class ReadmeOptions
{
    /// <summary>
    /// Whether package readme files are cached locally. Defaults to <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// When <see langword="false"/>, requests to readme endpoints are forwarded live to
    /// the upstream server on every request without storing a local copy. When
    /// <see langword="true"/> (the default), readme responses are stored in the configured
    /// cache backend and served from cache until the <see cref="CacheTtl"/> expires.
    /// Requires <c>Mirror:Cache:Enabled</c> to also be <see langword="true"/>.
    /// </remarks>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How long a cached readme file is considered fresh before it is re-validated
    /// against upstream. Defaults to 24 hours.
    /// </summary>
    public TimeSpan CacheTtl { get; set; } = TimeSpan.FromHours(24);
}
