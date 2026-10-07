namespace NuGetMirror.Configuration;

/// <summary>
/// Controls whether the mirror proxies and rewrites the upstream
/// RepositorySignatures resource.
/// </summary>
/// <remarks>
/// <b>Defaults to <c>false</c>.</b>
/// The mirror is typically run over HTTP, but some NuGet clients require
/// HTTPS for repository-signature URLs. Rewriting those URLs to the
/// mirror's HTTP base would break signature verification, so this
/// feature is opt-in. When it is off, the rewritten service index
/// preserves the upstream HTTPS URL and clients fetch signatures directly
/// from the upstream server.
/// </remarks>
public sealed class RepositorySignaturesOptions
{
    /// <summary>
    /// Whether the mirror proxies and rewrites the upstream RepositorySignatures
    /// service-index resource. Defaults to <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// See the class-level remarks for the rationale behind this opt-in flag.
    /// When <see langword="true"/>, the service index returned by the mirror will
    /// contain a <c>RepositorySignatures</c> entry pointing at the mirror's own
    /// <c>/v3/repository-signatures/</c> endpoints. Caching of the proxied
    /// signature data is controlled separately by
    /// <c>Mirror:Cache:RepositorySignatures:Enabled</c>.
    /// </remarks>
    public bool Enabled { get; set; }
}
