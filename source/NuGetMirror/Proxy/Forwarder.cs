using System.Net;
using System.Text;

using Microsoft.Extensions.Options;

using NuGetMirror.Configuration;
using NuGetMirror.Diagnostics;
using NuGetMirror.Discovery;
using NuGetMirror.Storage;
using NuGetMirror.Upstream;

namespace NuGetMirror.Proxy;

internal sealed partial class Forwarder(
    UpstreamClient client,
    DiscoveryCache cache,
    IOptions<MirrorOptions> options,
    ILogger<Forwarder> logger,
    MirrorMetrics metrics,
    IPackageContentStore? store = null,
    KeyedAsyncLock? keyedLock = null,
    TimeProvider? timeProvider = null,
    NegativeCache? negativeCache = null)
{
    private readonly CachedProxyPipeline? _pipeline = keyedLock is not null
        ? new CachedProxyPipeline(
            client, cache,
            store ?? throw new InvalidOperationException(
                $"An {nameof(IPackageContentStore)} must be registered when {nameof(KeyedAsyncLock)} is registered."),
            keyedLock, options, metrics, logger, timeProvider ?? TimeProvider.System, negativeCache)
        : null;

    private bool IsNegativeCacheEnabled => negativeCache is not null && options.Value.Cache.NegativeCache.Enabled && options.Value.Cache.Enabled;

    public async Task StreamProxyAsync(HttpContext context, string routePrefix, string? remainingPath)
    {
        ArgumentNullException.ThrowIfNull(context);

        CachedProxyPipeline? pipeline = _pipeline;
        bool cacheEnabled = options.Value.Cache.Enabled;
        string? cacheKey = cacheEnabled && pipeline is not null ? PackageCacheKey.TryCreate(remainingPath) : null;

        if (pipeline is not null && cacheKey is not null && store is not null)
        {
            if (HttpMethods.IsHead(context.Request.Method))
            {
                await StreamProxyCachedHeadAsync(context, routePrefix, remainingPath, cacheKey, store).ConfigureAwait(false);
            }
            else
            {
                await pipeline.RunCachedStreamAsync(context, CreatePackageGetRequest(routePrefix, remainingPath, cacheKey), context.RequestAborted).ConfigureAwait(false);
            }

            return;
        }

        await StreamProxyLiveAsync(context, routePrefix, remainingPath, CacheContentTypes.Package).ConfigureAwait(false);
    }

    public async Task ReadmeProxyAsync(HttpContext context, string routePrefix, string remainingPath)
    {
        ArgumentNullException.ThrowIfNull(context);

        CachedProxyPipeline? pipeline = _pipeline;
        if (!options.Value.Cache.Readme.Enabled || !options.Value.Cache.Enabled || pipeline is null)
        {
            await StreamProxyLiveAsync(context, routePrefix, remainingPath, CacheContentTypes.Readme).ConfigureAwait(false);
            return;
        }

        CancellationToken ct = context.RequestAborted;
        ReadmeOptions readmeOptions = options.Value.Cache.Readme;
        string cacheKey = PackageCacheKey.ReadmeCacheKey(remainingPath);

        var request = new CachedStreamRequest
        {
            CacheKey = cacheKey,
            ForwardKey = routePrefix,
            StageLabel = "readme",
            BuildUpstreamUri = (ctx, upstreamBase) => ProxyHttp.ComposeUpstreamUri(upstreamBase, remainingPath, ctx.Request.QueryString.Value),
            Telemetry = new ProxyFeatureTelemetry(
                OnRevalidated: metrics.RecordReadmeRevalidated,
                OnStaleFallback: metrics.RecordReadmeStaleFallback,
                OnCacheWriteSkipped: metrics.RecordCacheWriteSkipped),
            DefaultContentType = "text/markdown",
            SupportsHead = true,
            LruTouch = true,
            Ttl = readmeOptions.CacheTtl,
            StaleFallbackEnabled = true,
            CacheContentType = CacheContentTypes.Readme,
        };

        await pipeline.RunCachedStreamAsync(context, request, ct).ConfigureAwait(false);
    }

    public async Task RegistrationProxyAsync(HttpContext context, string routePrefix, string flavor, string? path)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (path is not null
            && (path.Contains("..", StringComparison.Ordinal) || path.Contains('\\')))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (!options.Value.Cache.Registration.Enabled || !options.Value.Cache.Enabled || _pipeline is null)
        {
            await RewriteProxyAsync(context, routePrefix, path).ConfigureAwait(false);
            return;
        }

        CancellationToken ct = context.RequestAborted;

        RegistrationCacheOptions regOptions = options.Value.Cache.Registration;
        string cacheKey = PackageCacheKey.RegistrationCacheKey(flavor, path ?? string.Empty);

        var request = new CachedRewriteIndexRequest
        {
            CacheKey = cacheKey,
            ForwardKey = routePrefix,
            StageLabel = "registration",
            BuildUpstreamUri = (ctx, upstreamBase) => ProxyHttp.ComposeUpstreamUri(upstreamBase, path, ctx.Request.QueryString.Value),
            RewriteBody = (body, mirrorBase) =>
            {
                DiscoverySnapshot? snap;
                try
                {
                    snap = cache.CurrentSnapshot;
                }
                catch (InvalidOperationException)
                {
                    snap = null;
                }
                return snap is null
                    ? body
                    : UrlRewriter.Rewrite(body, snap.RewritePairs, mirrorBase, snap.GetOrBuildRewriteTargets(mirrorBase));
            },
            Telemetry = new ProxyFeatureTelemetry(
                OnRevalidated: metrics.RecordRegistrationRevalidated,
                OnStaleFallback: metrics.RecordRegistrationStaleFallback,
                OnCacheWriteSkipped: metrics.RecordRegistrationCacheWriteSkipped),
            ResourceNotAdvertisedMessage = "Registration resource not advertised by upstream.",
            Ttl = regOptions.CacheTtl,
            MaxBodyBytes = regOptions.MaxBodyBytes,
            LruTouch = true,
            CacheContentType = CacheContentTypes.Registration,
        };

        await _pipeline.RunCachedRewriteIndexAsync(context, request, ct).ConfigureAwait(false);
    }

    private CachedStreamRequest CreatePackageGetRequest(string routePrefix, string? remainingPath, string cacheKey)
    {
        return new CachedStreamRequest
        {
            CacheKey = cacheKey,
            ForwardKey = routePrefix,
            StageLabel = "cached_get",
            BuildUpstreamUri = (ctx, upstreamBase) => ProxyHttp.ComposeUpstreamUri(upstreamBase, remainingPath, ctx.Request.QueryString.Value),
            Telemetry = new ProxyFeatureTelemetry(
                OnCacheWriteSkipped: metrics.RecordCacheWriteSkipped),
            DefaultContentType = "application/octet-stream",
            LruTouch = true,
            Ttl = null,
            StaleFallbackEnabled = false,
            UseNegativeCache = negativeCache is not null,
            NegCacheKey = routePrefix + (remainingPath ?? ""),
            CacheContentType = CacheContentTypes.Package,
        };
    }

    public async Task RewriteProxyAsync(HttpContext context, string routePrefix, string? remainingPath)
    {
        ArgumentNullException.ThrowIfNull(context);

        CancellationToken ct = context.RequestAborted;

        DiscoverySnapshot? snapshot = await GetSnapshotOrWriteErrorAsync(context, "rewrite", ct).ConfigureAwait(false);
        if (snapshot is null)
        {
            return;
        }

        if (!snapshot.ForwardMap.TryGetValue(routePrefix, out string? upstreamBase))
        {
            metrics.RecordProxyError("not_found", StatusCodes.Status404NotFound);
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        string mirrorBase = ProxyHttp.ResolveMirrorBase(context, options.Value.PublicBaseUrl, options.Value.NormalizedBasePath);
        Uri upstreamUri = ProxyHttp.ComposeUpstreamUri(upstreamBase, remainingPath, context.Request.QueryString.Value);

        await ProxyHttp.ExecuteUpstreamFetchAsync(
            context, metrics, "rewrite",
            fetch: () => FetchRewriteAndServeAsync(context, upstreamUri, snapshot, mirrorBase, routePrefix, remainingPath, ct),
            onFailure: (ex, kind) => HandleForwarderFailureAsync(context, ex, kind, "rewrite", routePrefix, remainingPath),
            handleUnexpected: false).ConfigureAwait(false);
    }

    private async Task FetchRewriteAndServeAsync(
        HttpContext context, Uri upstreamUri, DiscoverySnapshot snapshot,
        string mirrorBase, string routePrefix, string? remainingPath, CancellationToken ct)
    {
        using HttpResponseMessage response = await client.GetAsync(upstreamUri, stream: false, ct).ConfigureAwait(false);

        long maxBytes = options.Value.Upstream.MaxRewriteBodyBytes;
        byte[]? bodyBytes = await ProxyStreaming.ReadResponseBodyWithLimitAsync(
            context, response, maxBytes, "Upstream response body exceeds maximum allowed size.", metrics, ct).ConfigureAwait(false);
        if (bodyBytes is null)
        {
            return;
        }

        string body = Encoding.UTF8.GetString(bodyBytes);
        long rawUpstreamBytes = bodyBytes.LongLength;

        body = UrlRewriter.Rewrite(body, snapshot.RewritePairs, mirrorBase, snapshot.GetOrBuildRewriteTargets(mirrorBase));

        context.Response.StatusCode = (int)response.StatusCode;
        context.Response.ContentType = "application/json";

        metrics.RecordUpstreamBytes(rawUpstreamBytes);

        long servedBytes = await ProxyHttp.WriteBodyUtf8Async(context, body, ct).ConfigureAwait(false);
        metrics.RecordServedBytes(servedBytes, "upstream");

        LogProxySuccess("Rewrite", routePrefix, remainingPath);
    }

    private async Task StreamProxyCachedHeadAsync(HttpContext context, string routePrefix, string? remainingPath, string cacheKey, IPackageContentStore packageStore)
    {
        CancellationToken ct = context.RequestAborted;
        CachedContent? hit = await packageStore.TryGetAsync(cacheKey, ct).ConfigureAwait(false);

        if (hit is not null)
        {
            if (_pipeline is not null)
            {
                await _pipeline.TouchCacheHitAsync(cacheKey, ct).ConfigureAwait(false);
            }

            if (ConditionalRequest.TryWriteNotModified(context, hit.ETag))
            {
                await hit.Stream.DisposeAsync().ConfigureAwait(false);
                metrics.RecordClientNotModified("head", CacheContentTypes.Package);
                return;
            }

            LogCacheHeadHit(cacheKey);
            metrics.RecordCacheHeadHit(CacheContentTypes.Package);

            ProxyHttp.SetCachedContentHeaders(context, hit);
            await hit.Stream.DisposeAsync().ConfigureAwait(false);
            return;
        }

        string negCacheKey = routePrefix + (remainingPath ?? "");
        if (TryWriteNegativeCacheHitOr404(context, negCacheKey, CacheContentTypes.Package))
        {
            return;
        }

        metrics.RecordCacheMiss("head", CacheContentTypes.Package);
        await StreamProxyLiveAsync(context, routePrefix, remainingPath, CacheContentTypes.Package).ConfigureAwait(false);
    }

    private async Task StreamProxyLiveAsync(HttpContext context, string routePrefix, string? remainingPath, string contentType)
    {
        CancellationToken ct = context.RequestAborted;

        DiscoverySnapshot? snapshot = await GetSnapshotOrWriteErrorAsync(context, "live", ct).ConfigureAwait(false);
        if (snapshot is null)
        {
            return;
        }

        if (!snapshot.ForwardMap.TryGetValue(routePrefix, out string? upstreamBase))
        {
            metrics.RecordProxyError("not_found", StatusCodes.Status404NotFound);
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        Uri upstreamUri = ProxyHttp.ComposeUpstreamUri(upstreamBase, remainingPath, context.Request.QueryString.Value);
        string negCacheKey = routePrefix + (remainingPath ?? "");

        if (TryWriteNegativeCacheHitOr404(context, negCacheKey, contentType))
        {
            return;
        }

        await ProxyHttp.ExecuteUpstreamFetchAsync(
            context, metrics, "live",
            fetch: async () =>
            {
                using HttpResponseMessage response = await client.GetAsync(upstreamUri, stream: true, ct).ConfigureAwait(false);

                TryStoreNegativeCache(response, negCacheKey, contentType);

                context.Response.StatusCode = (int)response.StatusCode;
                ProxyHttp.CopyStreamHeaders(context, response);

                if (!HttpMethods.IsHead(context.Request.Method))
                {
                    if (response.Content.Headers.ContentLength is { } liveLength)
                    {
                        metrics.RecordUpstreamBytes(liveLength);
                        metrics.RecordServedBytes(liveLength, "upstream");
                    }

                    await response.Content.CopyToAsync(context.Response.Body, ct).ConfigureAwait(false);
                }

                LogProxySuccess("Live", routePrefix, remainingPath);
            },
            onFailure: (ex, kind) => HandleForwarderFailureAsync(context, ex, kind, "live", routePrefix, remainingPath),
            handleUnexpected: false).ConfigureAwait(false);
    }

    private async Task<DiscoverySnapshot?> GetSnapshotOrWriteErrorAsync(HttpContext context, string stage, CancellationToken ct)
    {
        try
        {
            return await cache.GetAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogDiscoveryFailed(ex, stage);
            metrics.RecordProxyError("discovery", StatusCodes.Status502BadGateway);
            await ProxyHttp.WriteProblemAsync(context, StatusCodes.Status502BadGateway, ex.Message).ConfigureAwait(false);
            return null;
        }
    }

    private bool TryWriteNegativeCacheHitOr404(HttpContext context, string negCacheKey, string contentType)
    {
        if (!IsNegativeCacheEnabled)
        {
            return false;
        }

        if (!negativeCache!.TryGet(negCacheKey))
        {
            return false;
        }

        metrics.RecordNegativeCacheHit(contentType);
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return true;
    }

    private void TryStoreNegativeCache(HttpResponseMessage response, string negCacheKey, string contentType)
    {
        if (!IsNegativeCacheEnabled)
        {
            return;
        }

        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            negativeCache!.Store(negCacheKey, options.Value.Cache.NegativeCache.Ttl);
            metrics.RecordNegativeCacheStore(contentType);
        }
    }

    private Task HandleForwarderFailureAsync(HttpContext context, Exception ex, UpstreamFailureKind kind, string stage, string routePrefix, string? remainingPath)
    {
        if (kind == UpstreamFailureKind.ResilienceRejected)
        {
            LogUpstreamResilienceRejected(ex, stage, routePrefix, remainingPath);
        }
        else
        {
            LogUpstreamFailed(ex, stage, routePrefix, remainingPath);
        }

        return ProxyHttp.WriteUpstreamErrorAsync(context, metrics, ex);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to retrieve discovery snapshot for {Stage} proxy.")]
    private partial void LogDiscoveryFailed(Exception ex, string stage);

    [LoggerMessage(Level = LogLevel.Error, Message = "Upstream request failed for {Stage} proxy {RoutePrefix}{Path}.")]
    private partial void LogUpstreamFailed(Exception ex, string stage, string routePrefix, string? path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Resilience policy rejected upstream request for {Stage} proxy {RoutePrefix}{Path}.")]
    private partial void LogUpstreamResilienceRejected(Exception ex, string stage, string routePrefix, string? path);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Cache head hit: {CacheKey}")]
    private partial void LogCacheHeadHit(string cacheKey);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Stage} proxy succeeded: {RoutePrefix}{Path}")]
    private partial void LogProxySuccess(string stage, string routePrefix, string? path);
}
