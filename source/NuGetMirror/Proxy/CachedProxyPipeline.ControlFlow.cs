using System.Net;

using NuGetMirror.Discovery;
using NuGetMirror.Storage;

namespace NuGetMirror.Proxy;

internal sealed partial class CachedProxyPipeline
{
    private async Task<bool> TryServeFreshStreamAsync(HttpContext context, CachedContent? cached, CachedStreamRequest request, bool afterLock, CancellationToken ct)
    {
        if (cached is null || !IsCacheFresh(cached, request.Ttl))
        {
            return false;
        }

        if (request.LruTouch)
        {
            await TouchCacheHitAsync(request.CacheKey, ct).ConfigureAwait(false);
        }

        if (afterLock)
        {
            LogCacheHitAfterLock(logger, request.CacheKey);
            metrics.RecordCacheHitAfterLock(request.CacheContentType);
        }
        else
        {
            LogCacheHit(logger, request.CacheKey);
            metrics.RecordCacheHit(ProxyHttp.MethodTag(context), request.CacheContentType);
        }

        await ServeCachedStreamAsync(context, cached, request.SupportsHead, request.CacheContentType, ct).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> TryServeFreshRewriteIndexAsync(HttpContext context, CachedContent? cached, CachedRewriteIndexRequest request, string mirrorBase, bool afterLock, CancellationToken ct)
    {
        if (cached is null || !IsCacheFresh(cached, request.Ttl))
        {
            return false;
        }

        if (request.LruTouch)
        {
            await TouchCacheHitAsync(request.CacheKey, ct).ConfigureAwait(false);
        }

        if (afterLock)
        {
            LogCacheHitAfterLock(logger, request.CacheKey);
            metrics.RecordCacheHitAfterLock(request.CacheContentType);
        }
        else
        {
            LogCacheHit(logger, request.CacheKey);
            metrics.RecordCacheHit(ProxyHttp.MethodTag(context), request.CacheContentType);
        }

        await ServeRewriteIndexFromCacheAsync(context, cached, request, mirrorBase, ct).ConfigureAwait(false);
        return true;
    }

    private bool TryServeNegativeCacheHit(HttpContext context, CachedStreamRequest request)
    {
        if (!request.UseNegativeCache || negativeCache is null || !options.Value.Cache.NegativeCache.Enabled
            || request.NegCacheKey is not { } negCacheKey || !negativeCache.TryGet(negCacheKey))
        {
            return false;
        }

        metrics.RecordNegativeCacheHit(request.CacheContentType);
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return true;
    }

    /// <summary>
    /// Resolves the upstream URI for a stream request. Returns <see langword="null"/> when the
    /// response has already been completed — discovery failure (502), forward-map miss served
    /// from stale cache, or forward-map miss with no cache (404) — and the caller must return.
    /// </summary>
    private async Task<Uri?> ResolveUpstreamUriAsync(HttpContext context, CachedContent? cached, CachedStreamRequest request, CancellationToken ct)
    {
        if (request.UpstreamUri is not null)
        {
            return request.UpstreamUri;
        }

        DiscoverySnapshot snapshot;
        try
        {
            snapshot = await discovery.GetAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogDiscoveryFailed(logger, ex, request.StageLabel);
            metrics.RecordProxyError("discovery", StatusCodes.Status502BadGateway);
            await ProxyHttp.WriteProblemAsync(context, StatusCodes.Status502BadGateway, ex.Message).ConfigureAwait(false);
            return null;
        }

        if (snapshot.ForwardMap.TryGetValue(request.ForwardKey, out string? upstreamBase))
        {
            return request.BuildUpstreamUri(context, upstreamBase);
        }

        if (cached is not null)
        {
            request.Telemetry.OnStaleFallback?.Invoke();
            await ServeCachedStreamAsync(context, cached, request.SupportsHead, request.CacheContentType, ct).ConfigureAwait(false);
            return null;
        }

        metrics.RecordProxyError("not_found", StatusCodes.Status404NotFound);
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return null;
    }
}
