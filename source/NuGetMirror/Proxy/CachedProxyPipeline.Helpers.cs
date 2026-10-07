using System.Net;

using NuGetMirror.Storage;

namespace NuGetMirror.Proxy;

internal sealed partial class CachedProxyPipeline
{
    // ── Error-response helpers ──────────────────────────────────────────────

    /// <summary>
    /// Serves a stale cached stream when available, or writes a 502 problem response.
    /// Called as the failure callback from <see cref="ProxyHttp.ExecuteUpstreamFetchAsync"/>.
    /// </summary>
    private async Task HandleStreamErrorAsync(
        HttpContext context, CachedContent? cached, CachedStreamRequest request,
        Exception ex, UpstreamFailureKind kind, CancellationToken ct)
    {
        if (kind == UpstreamFailureKind.ResilienceRejected)
        {
            LogUpstreamResilienceRejected(logger, ex, request.StageLabel, request.CacheKey);
        }
        else
        {
            LogUpstreamFailed(logger, ex, request.StageLabel, request.CacheKey);
        }

        if (cached is not null && !context.Response.HasStarted)
        {
            request.Telemetry.OnStaleFallback?.Invoke();
            await ServeCachedStreamAsync(context, cached, request.SupportsHead, request.CacheContentType, ct).ConfigureAwait(false);
            return;
        }

        if (!context.Response.HasStarted)
        {
            string errorType = kind == UpstreamFailureKind.Unexpected ? "internal" : "upstream";
            metrics.RecordProxyError(errorType, StatusCodes.Status502BadGateway);
            await ProxyHttp.WriteProblemAsync(context, StatusCodes.Status502BadGateway, ex.Message).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Serves a stale rewritten-index response when available, or writes a 502 problem response.
    /// Called as the failure callback from <see cref="ProxyHttp.ExecuteUpstreamFetchAsync"/>.
    /// </summary>
    private async Task HandleRewriteIndexErrorAsync(
        HttpContext context, CachedContent? cached, CachedRewriteIndexRequest request,
        string mirrorBase, Exception ex, UpstreamFailureKind kind, CancellationToken ct)
    {
        if (kind == UpstreamFailureKind.ResilienceRejected)
        {
            LogUpstreamResilienceRejected(logger, ex, request.StageLabel, request.CacheKey);
        }
        else
        {
            LogUpstreamFailed(logger, ex, request.StageLabel, request.CacheKey);
        }

        if (cached is not null && !context.Response.HasStarted)
        {
            request.Telemetry.OnStaleFallback?.Invoke();
            await ServeRewriteIndexFromCacheAsync(context, cached, request, mirrorBase, ct).ConfigureAwait(false);
            return;
        }

        if (!context.Response.HasStarted)
        {
            string errorType = kind == UpstreamFailureKind.Unexpected ? "internal" : "upstream";
            metrics.RecordProxyError(errorType, StatusCodes.Status502BadGateway);
            await ProxyHttp.WriteProblemAsync(context, StatusCodes.Status502BadGateway, ex.Message).ConfigureAwait(false);
        }
    }

    // ── Stream fetch phase helpers ──────────────────────────────────────────

    /// <summary>
    /// Handles a 304 Not Modified response for a stream request.
    /// Called from <see cref="FetchAndStreamFromUpstreamAsync"/>.
    /// </summary>
    private async Task HandleNotModifiedStreamAsync(
        HttpContext context, CachedContent cached, CachedStreamRequest request, CancellationToken ct)
    {
        await RefreshCacheTimestampAsync(request.CacheKey, cached.ContentType, cached.ETag, ct).ConfigureAwait(false);
        request.Telemetry.OnRevalidated?.Invoke();
        await ServeCachedStreamAsync(context, cached, request.SupportsHead, request.CacheContentType, ct).ConfigureAwait(false);
    }

    /// <summary>Handles a non-success upstream response for a stream request.</summary>
    private async Task HandleNonSuccessStreamAsync(
        HttpContext context, CachedContent? cached, CachedStreamRequest request,
        HttpResponseMessage response, CancellationToken ct)
    {
        request.Telemetry.OnCacheWriteSkipped?.Invoke();

        if (request.UseNegativeCache && negativeCache is not null && options.Value.Cache.NegativeCache.Enabled
            && response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone
            && request.NegCacheKey is { } negCacheKey)
        {
            negativeCache.Store(negCacheKey, options.Value.Cache.NegativeCache.Ttl);
            metrics.RecordNegativeCacheStore(request.CacheContentType);
        }

        if (request.StaleFallbackEnabled && cached is not null)
        {
            request.Telemetry.OnStaleFallback?.Invoke();
            await ServeCachedStreamAsync(context, cached, request.SupportsHead, request.CacheContentType, ct).ConfigureAwait(false);
            return;
        }

        context.Response.StatusCode = (int)response.StatusCode;
    }

    /// <summary>
    /// Handles an upstream response that lacks a <c>Content-Length</c> header.
    /// Falls back to a stale cache entry when enabled, otherwise streams the response without caching.
    /// </summary>
    private async Task HandleMissingContentLengthAsync(
        HttpContext context, CachedContent? cached, CachedStreamRequest request,
        HttpResponseMessage response, CancellationToken ct)
    {
        LogNotCaching(logger, request.CacheKey, (int)response.StatusCode);
        request.Telemetry.OnCacheWriteSkipped?.Invoke();

        if (request.StaleFallbackEnabled && cached is not null)
        {
            request.Telemetry.OnStaleFallback?.Invoke();
            await ServeCachedStreamAsync(context, cached, request.SupportsHead, request.CacheContentType, ct).ConfigureAwait(false);
            return;
        }

        await ProxyHttp.StreamResponseWithoutCachingAsync(context, response, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Streams an upstream response body to the client while simultaneously writing it to the cache.
    /// </summary>
    private async Task StreamAndCacheResponseAsync(
        HttpContext context, CachedStreamRequest request,
        HttpResponseMessage response, long contentLength, CancellationToken ct)
    {
        string contentType = response.Content.Headers.ContentType?.ToString() ?? request.DefaultContentType;
        string? etag = response.Headers.ETag?.Tag;

        ICacheWriteHandle? writeHandle = null;

        try
        {
            writeHandle = await store.BeginWriteAsync(request.CacheKey, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogCacheWriteFailed(logger, ex, request.CacheKey);
            metrics.RecordCacheWriteFailed();
        }

        if (writeHandle is null)
        {
            metrics.RecordUpstreamBytes(contentLength);
            metrics.RecordServedBytes(contentLength, "upstream");

            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.ContentType = contentType;
            context.Response.ContentLength = contentLength;

            if (etag is not null)
            {
                context.Response.Headers.ETag = etag;
            }

            if (!request.SupportsHead || !HttpMethods.IsHead(context.Request.Method))
            {
                await response.Content.CopyToAsync(context.Response.Body, ct).ConfigureAwait(false);
            }

            return;
        }

        writeHandle.SetMetadata(contentType, contentLength, etag);

        try
        {
            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.ContentType = contentType;
            context.Response.ContentLength = contentLength;

            if (etag is not null)
            {
                context.Response.Headers.ETag = etag;
            }

            if (request.SupportsHead && HttpMethods.IsHead(context.Request.Method))
            {
                return;
            }

            Stream upstreamStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

            try
            {
                (long copied, bool cacheOk) = await ProxyStreaming.TeeStreamAsync(upstreamStream, context.Response.Body, writeHandle.Stream, request.CacheKey, logger, ct).ConfigureAwait(false);
                metrics.RecordUpstreamBytes(copied);
                metrics.RecordServedBytes(copied, "upstream");

                if (cacheOk && copied == contentLength)
                {
                    await writeHandle.CommitAsync(ct).ConfigureAwait(false);
                    LogCacheWriteSuccess(logger, request.CacheKey, contentLength);
                    metrics.RecordCacheWriteCommitted(contentLength);
                }
            }
            finally
            {
                await upstreamStream.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            await writeHandle.DisposeAsync().ConfigureAwait(false);
        }
    }

    // ── Rewrite-index fetch helpers ─────────────────────────────────────────

    /// <summary>Writes a response body to the cache store as an <c>application/json</c> entry.</summary>
    private async Task WriteCacheBodyAsync(
        string cacheKey, ReadOnlyMemory<byte> bodyBytes, long contentLength, string? etag, CancellationToken ct)
    {
        try
        {
            ICacheWriteHandle writeHandle = await store.BeginWriteAsync(cacheKey, ct).ConfigureAwait(false);
            try
            {
                writeHandle.SetMetadata("application/json", contentLength, etag);
                await writeHandle.Stream.WriteAsync(bodyBytes, ct).ConfigureAwait(false);
                await writeHandle.Stream.FlushAsync(ct).ConfigureAwait(false);
                await writeHandle.CommitAsync(ct).ConfigureAwait(false);
                LogCacheWriteSuccess(logger, cacheKey, contentLength);
                metrics.RecordCacheWriteCommitted(contentLength);
            }
            finally
            {
                await writeHandle.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogCacheWriteFailed(logger, ex, cacheKey);
            metrics.RecordCacheWriteFailed();
        }
    }

    // ── Shared helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Refreshes the cached entry's timestamp via <see cref="ICacheRevalidation"/> when the store supports it.
    /// Exceptions are swallowed and logged — a missed timestamp update is non-fatal.
    /// </summary>
    private async Task RefreshCacheTimestampAsync(string cacheKey, string contentType, string? etag, CancellationToken ct)
    {
        if (store is ICacheRevalidation revalidation)
        {
            try
            {
                await revalidation.RefreshTimestampAsync(cacheKey, contentType, etag, timeProvider.GetUtcNow(), ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogRefreshTimestampFailed(logger, ex, cacheKey);
            }
        }
    }
}
