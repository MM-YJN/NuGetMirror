using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

using Microsoft.Extensions.Options;

using NuGetMirror.Configuration;
using NuGetMirror.Diagnostics;
using NuGetMirror.Discovery;
using NuGetMirror.Storage;
using NuGetMirror.Upstream;

using Polly;

namespace NuGetMirror.Proxy;

internal sealed partial class CachedProxyPipeline(
    UpstreamClient client,
    DiscoveryCache discovery,
    IPackageContentStore store,
    KeyedAsyncLock keyedLock,
    IOptions<MirrorOptions> options,
    MirrorMetrics metrics,
    ILogger logger,
    TimeProvider timeProvider,
    NegativeCache? negativeCache = null)
{
    public async Task RunCachedStreamAsync(HttpContext context, CachedStreamRequest request, CancellationToken ct)
    {
        CachedContent? cached = await store.TryGetAsync(request.CacheKey, ct).ConfigureAwait(false);

        if (await TryServeFreshStreamAsync(context, cached, request, afterLock: false, ct).ConfigureAwait(false))
        {
            return;
        }

        using IDisposable releaser = await keyedLock.LockAsync(request.CacheKey, ct).ConfigureAwait(false);

        cached = await store.TryGetAsync(request.CacheKey, ct).ConfigureAwait(false);

        if (await TryServeFreshStreamAsync(context, cached, request, afterLock: true, ct).ConfigureAwait(false))
        {
            return;
        }

        LogCacheMiss(logger, request.CacheKey);
        metrics.RecordCacheMiss(ProxyHttp.MethodTag(context), request.CacheContentType);

        if (TryServeNegativeCacheHit(context, request))
        {
            return;
        }

        Uri? upstreamUri = await ResolveUpstreamUriAsync(context, cached, request, ct).ConfigureAwait(false);
        if (upstreamUri is null)
        {
            return;
        }

        await ProxyHttp.ExecuteUpstreamFetchAsync(
            context, metrics, request.StageLabel,
            fetch: () => FetchAndStreamFromUpstreamAsync(context, cached, request, upstreamUri, ct),
            onFailure: (ex, kind) => HandleStreamErrorAsync(context, cached, request, ex, kind, ct),
            handleUnexpected: true).ConfigureAwait(false);
    }

    public async Task RunCachedRewriteIndexAsync(HttpContext context, CachedRewriteIndexRequest request, CancellationToken ct)
    {
        string mirrorBase = ProxyHttp.ResolveMirrorBase(context, options.Value.PublicBaseUrl, options.Value.NormalizedBasePath);

        CachedContent? cached = await store.TryGetAsync(request.CacheKey, ct).ConfigureAwait(false);

        if (await TryServeFreshRewriteIndexAsync(context, cached, request, mirrorBase, afterLock: false, ct).ConfigureAwait(false))
        {
            return;
        }

        using IDisposable releaser = await keyedLock.LockAsync(request.CacheKey, ct).ConfigureAwait(false);

        cached = await store.TryGetAsync(request.CacheKey, ct).ConfigureAwait(false);

        if (await TryServeFreshRewriteIndexAsync(context, cached, request, mirrorBase, afterLock: true, ct).ConfigureAwait(false))
        {
            return;
        }

        LogCacheMiss(logger, request.CacheKey);
        metrics.RecordCacheMiss(ProxyHttp.MethodTag(context), request.CacheContentType);

        await ProxyHttp.ExecuteUpstreamFetchAsync(
            context, metrics, request.StageLabel,
            fetch: () => FetchAndRewriteIndexFromUpstreamAsync(context, cached, request, mirrorBase, ct),
            onFailure: (ex, kind) => HandleRewriteIndexErrorAsync(context, cached, request, mirrorBase, ex, kind, ct),
            handleUnexpected: true).ConfigureAwait(false);
    }

    private async Task ServeCachedStreamAsync(HttpContext context, CachedContent cached, bool supportsHead, string contentType, CancellationToken ct)
    {
        try
        {
            if (ConditionalRequest.TryWriteNotModified(context, cached.ETag))
            {
                metrics.RecordClientNotModified(ProxyHttp.MethodTag(context), contentType);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = cached.ContentType;
            context.Response.ContentLength = cached.Length;

            if (cached.ETag is not null)
            {
                context.Response.Headers.ETag = cached.ETag;
            }

            if (!supportsHead || !HttpMethods.IsHead(context.Request.Method))
            {
                await cached.Stream.CopyToAsync(context.Response.Body, ct).ConfigureAwait(false);
                metrics.RecordServedBytes(cached.Length, "cache");
            }
        }
        finally
        {
            await cached.Stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task FetchAndStreamFromUpstreamAsync(
        HttpContext context, CachedContent? cached, CachedStreamRequest request,
        Uri upstreamUri, CancellationToken ct)
    {
        bool supportsRevalidation = request.Ttl is not null;
        string? ifNoneMatch = supportsRevalidation ? cached?.ETag : null;

        using HttpResponseMessage response = await client.GetAsync(upstreamUri, stream: true, ct, ifNoneMatch).ConfigureAwait(false);

        if (supportsRevalidation && response.StatusCode == HttpStatusCode.NotModified && cached is not null)
        {
            await HandleNotModifiedStreamAsync(context, cached, request, ct).ConfigureAwait(false);
            return;
        }

        if (!response.IsSuccessStatusCode)
        {
            await HandleNonSuccessStreamAsync(context, cached, request, response, ct).ConfigureAwait(false);
            return;
        }

        long? declaredLength = response.Content.Headers.ContentLength;
        if (request.MaxBodyBytes is { } maxBytes)
        {
            byte[]? body = await ProxyStreaming.ReadResponseBodyWithLimitAsync(
                context, response, maxBytes, "Upstream response body exceeds maximum allowed size.", metrics, ct).ConfigureAwait(false);
            if (body is null)
            {
                return;
            }

            MediaTypeHeaderValue? contentType = response.Content.Headers.ContentType;
            response.Content.Dispose();
            response.Content = new ByteArrayContent(body);
            response.Content.Headers.ContentType = contentType;
        }

        if (declaredLength is not { } contentLength)
        {
            await HandleMissingContentLengthAsync(context, cached, request, response, ct).ConfigureAwait(false);
            return;
        }

        await StreamAndCacheResponseAsync(context, request, response, contentLength, ct).ConfigureAwait(false);
    }

    private async Task FetchAndRewriteIndexFromUpstreamAsync(
        HttpContext context, CachedContent? cached, CachedRewriteIndexRequest request,
        string mirrorBase, CancellationToken ct)
    {
        DiscoverySnapshot snapshot = await discovery.GetAsync(ct).ConfigureAwait(false);

        if (!snapshot.ForwardMap.TryGetValue(request.ForwardKey, out string? upstreamBase))
        {
            if (cached is not null)
            {
                request.Telemetry.OnStaleFallback?.Invoke();
                await ServeRewriteIndexFromCacheAsync(context, cached, request, mirrorBase, ct).ConfigureAwait(false);
                return;
            }

            await ProxyHttp.WriteProblemAsync(context, StatusCodes.Status502BadGateway, request.ResourceNotAdvertisedMessage).ConfigureAwait(false);
            return;
        }

        Uri upstreamUri = request.BuildUpstreamUri(context, upstreamBase);
        string? ifNoneMatch = cached?.ETag;

        using HttpResponseMessage response = await client.GetAsync(upstreamUri, stream: false, ct, ifNoneMatch).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotModified && cached is not null)
        {
            await RefreshCacheTimestampAsync(request.CacheKey, "application/json", cached.ETag, ct).ConfigureAwait(false);
            request.Telemetry.OnRevalidated?.Invoke();
            await ServeRewriteIndexFromCacheAsync(context, cached, request, mirrorBase, ct).ConfigureAwait(false);
            return;
        }

        if (!response.IsSuccessStatusCode)
        {
            request.Telemetry.OnCacheWriteSkipped?.Invoke();

            if (cached is not null)
            {
                request.Telemetry.OnStaleFallback?.Invoke();
                await ServeRewriteIndexFromCacheAsync(context, cached, request, mirrorBase, ct).ConfigureAwait(false);
                return;
            }

            context.Response.StatusCode = (int)response.StatusCode;
            return;
        }

        bool hasContentLength = response.Content.Headers.ContentLength is not null;

        byte[]? bodyBytes = await ProxyStreaming.ReadResponseBodyWithLimitAsync(
            context, response, request.MaxBodyBytes, "Upstream response body exceeds maximum allowed size.", metrics, ct).ConfigureAwait(false);

        if (bodyBytes is null)
        {
            return;
        }

        string? etag = response.Headers.ETag?.Tag;
        long contentLength = (long)bodyBytes.Length;

        if (hasContentLength)
        {
            await WriteCacheBodyAsync(request.CacheKey, bodyBytes, contentLength, etag, ct).ConfigureAwait(false);
        }
        else
        {
            request.Telemetry.OnCacheWriteSkipped?.Invoke();
        }

        string body = Encoding.UTF8.GetString(bodyBytes);
        body = request.RewriteBody(body, mirrorBase);
        long servedBytes = await ProxyHttp.WriteJsonResponseAsync(context, HttpStatusCode.OK, body, ct).ConfigureAwait(false);
        metrics.RecordServedBytes(servedBytes, "upstream");
    }

    private async Task ServeRewriteIndexFromCacheAsync(
        HttpContext context, CachedContent cached, CachedRewriteIndexRequest request,
        string mirrorBase, CancellationToken ct)
    {
        try
        {
            if (!string.IsNullOrEmpty(options.Value.PublicBaseUrl)
                && ConditionalRequest.TryWriteNotModified(context, cached.ETag))
            {
                metrics.RecordClientNotModified("get", request.CacheContentType);
                return;
            }

            string body;
            if (cached.Length is < 0 or > int.MaxValue)
            {
                LogCacheEntryInvalidSize(logger, request.CacheKey, cached.Length);
                metrics.RecordProxyError("internal", StatusCodes.Status500InternalServerError);
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                return;
            }

            // MaxBodyBytes is configured by callers and defaults to 256 KB; a long→int cast
            // is safe here because the guard above rejects values outside int range.
            int cachedLength = (int)cached.Length;
            byte[] bodyBytes = ArrayPool<byte>.Shared.Rent(cachedLength);
            try
            {
                await cached.Stream.ReadExactlyAsync(bodyBytes.AsMemory(0, cachedLength), ct).ConfigureAwait(false);
                body = Encoding.UTF8.GetString(bodyBytes, 0, cachedLength);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(bodyBytes);
            }

            body = request.RewriteBody(body, mirrorBase);

            if (!string.IsNullOrEmpty(options.Value.PublicBaseUrl))
            {
                context.Response.Headers.ETag = cached.ETag;
            }

            long servedBytes = await ProxyHttp.WriteJsonResponseAsync(context, HttpStatusCode.OK, body, ct).ConfigureAwait(false);
            metrics.RecordServedBytes(servedBytes, "cache");
        }
        finally
        {
            await cached.Stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal async Task TouchCacheHitAsync(string cacheKey, CancellationToken ct)
    {
        CacheEvictionOptions eviction = options.Value.Cache.Eviction;

        if (!eviction.Enabled || eviction.Strategy != EvictionStrategy.Lru || store is not ICacheMaintenance maintenance)
        {
            return;
        }

        try
        {
            await maintenance.TouchAsync(cacheKey, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogTouchCacheFailed(logger, ex, cacheKey);
        }
    }

    private bool IsCacheFresh(CachedContent cached, TimeSpan? ttl)
    {
        if (ttl is { } ttlValue)
        {
            return cached.StoredAtUtc is { } storedAt && timeProvider.GetUtcNow() - storedAt < ttlValue;
        }

        return true;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Cache hit: {CacheKey}")]
    private static partial void LogCacheHit(ILogger logger, string cacheKey);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Cache hit (after lock): {CacheKey}")]
    private static partial void LogCacheHitAfterLock(ILogger logger, string cacheKey);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Cache miss: {CacheKey}")]
    private static partial void LogCacheMiss(ILogger logger, string cacheKey);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Cached response written: {CacheKey} ({Length} bytes)")]
    private static partial void LogCacheWriteSuccess(ILogger logger, string cacheKey, long length);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to write to cache: {CacheKey}")]
    private static partial void LogCacheWriteFailed(ILogger logger, Exception ex, string cacheKey);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to retrieve discovery snapshot for {StageLabel} proxy.")]
    private static partial void LogDiscoveryFailed(ILogger logger, Exception ex, string stageLabel);

    [LoggerMessage(Level = LogLevel.Error, Message = "Upstream request failed for {StageLabel} proxy (key: {CacheKey}).")]
    private static partial void LogUpstreamFailed(ILogger logger, Exception ex, string stageLabel, string cacheKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Resilience policy rejected upstream request for {StageLabel} proxy (key: {CacheKey}).")]
    private static partial void LogUpstreamResilienceRejected(ILogger logger, Exception ex, string stageLabel, string cacheKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to refresh timestamp for cache entry {CacheKey}.")]
    private static partial void LogRefreshTimestampFailed(ILogger logger, Exception ex, string cacheKey);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Not caching upstream response: {CacheKey} (status={StatusCode})")]
    private static partial void LogNotCaching(ILogger logger, string cacheKey, int statusCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to update cache entry timestamp for LRU eviction: {CacheKey}.")]
    private static partial void LogTouchCacheFailed(ILogger logger, Exception ex, string cacheKey);

    [LoggerMessage(Level = LogLevel.Error, Message = "Cache entry {CacheKey} has invalid size ({Size}); serving 500.")]
    private static partial void LogCacheEntryInvalidSize(ILogger logger, string cacheKey, long size);
}
