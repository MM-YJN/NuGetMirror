using Microsoft.Extensions.Options;

using NuGetMirror.Configuration;
using NuGetMirror.Diagnostics;
using NuGetMirror.Discovery;
using NuGetMirror.Storage;
using NuGetMirror.Upstream;

namespace NuGetMirror.Proxy;

internal sealed class RepositorySignaturesForwarder(
    UpstreamClient client,
    DiscoveryCache discovery,
    IPackageContentStore store,
    KeyedAsyncLock keyedLock,
    IOptions<MirrorOptions> options,
    ILogger<RepositorySignaturesForwarder> logger,
    MirrorMetrics metrics,
    TimeProvider timeProvider)
{
    private const string CertificateCachePrefix = "$reposign/certificates/";

    private readonly CachedProxyPipeline _pipeline = new(client, discovery, store, keyedLock, options, metrics, logger, timeProvider);

    public async Task IndexHandlerAsync(HttpContext context, string version)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(version);

        if (!options.Value.RepositorySignatures.Enabled
            || !options.Value.Cache.RepositorySignatures.Enabled
            || !options.Value.Cache.Enabled)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        CancellationToken ct = context.RequestAborted;
        RepositorySignaturesCacheOptions reposignOptions = options.Value.Cache.RepositorySignatures;
        string cacheKey = $"$reposign/{version}/index.json";

        var request = new CachedRewriteIndexRequest
        {
            CacheKey = cacheKey,
            ForwardKey = "/v3/repository-signatures/",
            StageLabel = "reposign_index",
            BuildUpstreamUri = (ctx, upstreamBase) => new Uri($"{upstreamBase}{version}/index.json"),
            RewriteBody = (body, mirrorBase) =>
            {
                DiscoverySnapshot? snapshot = TryGetSnapshot();

                if (snapshot is null)
                {
                    return body;
                }

                return UrlRewriter.Rewrite(body, snapshot.RewritePairs, mirrorBase, snapshot.GetOrBuildRewriteTargets(mirrorBase));
            },
            Telemetry = new ProxyFeatureTelemetry(
                OnRevalidated: metrics.RecordRepositorySignaturesRevalidated,
                OnStaleFallback: metrics.RecordRepositorySignaturesStaleFallback,
                OnCacheWriteSkipped: metrics.RecordRepositorySignaturesCacheWriteSkipped),
            ResourceNotAdvertisedMessage = "RepositorySignatures resource not advertised by upstream.",
            Ttl = reposignOptions.CacheTtl,
            MaxBodyBytes = reposignOptions.MaxBodyBytes,
            CacheContentType = CacheContentTypes.RepositorySignatureIndex,
        };

        await _pipeline.RunCachedRewriteIndexAsync(context, request, ct).ConfigureAwait(false);
    }

    public async Task CertificateHandlerAsync(HttpContext context, string path)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(path);

        if (!options.Value.RepositorySignatures.Enabled
            || !options.Value.Cache.RepositorySignatures.Enabled
            || !options.Value.Cache.Enabled)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (string.IsNullOrWhiteSpace(path)
            || path.Contains("..", StringComparison.Ordinal)
            || path.Contains('\\'))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        CancellationToken ct = context.RequestAborted;
        RepositorySignaturesCacheOptions reposignOptions = options.Value.Cache.RepositorySignatures;
        string cacheKey = $"{CertificateCachePrefix}{path}";

        var request = new CachedStreamRequest
        {
            CacheKey = cacheKey,
            ForwardKey = "/v3/repository-signatures/",
            StageLabel = "reposign_cert",
            BuildUpstreamUri = (ctx, upstreamBase) => new Uri($"{upstreamBase}certificates/{path}"),
            Telemetry = new ProxyFeatureTelemetry(
                OnRevalidated: metrics.RecordRepositorySignaturesRevalidated,
                OnStaleFallback: metrics.RecordRepositorySignaturesStaleFallback,
                OnCacheWriteSkipped: metrics.RecordRepositorySignaturesCacheWriteSkipped),
            DefaultContentType = "application/octet-stream",
            SupportsHead = true,
            LruTouch = true,
            Ttl = reposignOptions.CacheTtl,
            StaleFallbackEnabled = true,
            CacheContentType = CacheContentTypes.RepositorySignatureCertificate,
        };

        await _pipeline.RunCachedStreamAsync(context, request, ct).ConfigureAwait(false);
    }

    private DiscoverySnapshot? TryGetSnapshot()
    {
        try
        {
            return discovery.CurrentSnapshot;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
