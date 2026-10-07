using System.Net;
using System.Net.Http.Headers;
using System.Text;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using NuGetMirror.Configuration;
using NuGetMirror.Diagnostics;
using NuGetMirror.Discovery;
using NuGetMirror.Proxy;
using NuGetMirror.Storage;
using NuGetMirror.TestKit;
using NuGetMirror.Upstream;

using Polly;

namespace NuGetMirror.UnitTests;

public sealed class CachedProxyPipelineStreamTests
{
    private const string CacheKey = "test.pkg/1.0.0/test.pkg.1.0.0.nupkg";
    private const string ForwardKey = "/v3-flatcontainer/";
    private const string UpstreamBase = "http://upstream.example/v3-flatcontainer/";
    private const string RemainingPath = "test.pkg/1.0.0/test.pkg.1.0.0.nupkg";
    private static readonly Uri s_upstreamUri = new(UpstreamBase + RemainingPath);
    private static readonly byte[] s_bodyBytes = "upstream-package-content"u8.ToArray();

    // ── Cache hit (fresh) ───────────────────────────────────────────────────

    [Fact]
    public async Task RunCachedStream_CacheHit_Fresh_ServesFromCache()
    {
        byte[] cachedBytes = "cached-content"u8.ToArray();
        var store = new InMemoryStore { CachedBytes = cachedBytes };
        CachedProxyPipeline pipeline = CreatePipeline(store, new StubUpstreamHandler());

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        await pipeline.RunCachedStreamAsync(context, MakeStreamRequest(ttl: TimeSpan.FromMinutes(30)), TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(cachedBytes, ReadBody(context));
    }

    [Fact]
    public async Task RunCachedStream_CacheHit_Fresh_DisposesCachedStream()
    {
        var cachedStream = new DisposeTrackingStream(new MemoryStream("cached"u8.ToArray()));
        var store = new InMemoryStore { CachedStream = cachedStream, Length = 5 };
        CachedProxyPipeline pipeline = CreatePipeline(store, new StubUpstreamHandler());

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        await pipeline.RunCachedStreamAsync(context, MakeStreamRequest(ttl: TimeSpan.FromMinutes(30)), TestContext.Current.CancellationToken);

        Assert.True(cachedStream.DisposeAsyncCalled || cachedStream.DisposeCalled,
            "Cached stream must be disposed after serving.");
    }

    // ── Cache hit (stale) → upstream fetch ──────────────────────────────────

    [Fact]
    public async Task RunCachedStream_CacheHit_StaleBeyondTtl_FetchesFromUpstream()
    {
        DateTimeOffset staleTime = DateTimeOffset.UtcNow - TimeSpan.FromHours(1);
        var store = new InMemoryStore { CachedBytes = "stale"u8.ToArray(), StoredAt = staleTime };
        var handler = new StubUpstreamHandler();
        handler.MapBytes(s_upstreamUri.ToString(), s_bodyBytes, "application/octet-stream");
        CachedProxyPipeline pipeline = CreatePipeline(store, handler);

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        await pipeline.RunCachedStreamAsync(context, MakeStreamRequest(ttl: TimeSpan.FromMinutes(30)), TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(s_bodyBytes, ReadBody(context));
        Assert.Equal(1, handler.GetCount(s_upstreamUri.ToString()));
    }

    // ── Cache hit after lock (double-check) ─────────────────────────────────

    [Fact]
    public async Task RunCachedStream_DoubleCheckAfterLock_ServesFromCache()
    {
        byte[] cachedBytes = "filled-by-other-thread"u8.ToArray();
        var store = new DelayedHitStore(cachedBytes, hitOnAttempt: 2);
        var handler = new StubUpstreamHandler();
        CachedProxyPipeline pipeline = CreatePipeline(store, handler);

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        await pipeline.RunCachedStreamAsync(context, MakeStreamRequest(ttl: TimeSpan.FromMinutes(30)), TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(cachedBytes, ReadBody(context));
        Assert.Equal(0, handler.GetCount(s_upstreamUri.ToString()));
    }

    // ── LRU touch on cache hit ──────────────────────────────────────────────

    [Fact]
    public async Task RunCachedStream_CacheHit_LruTouch_CallsTouchAsync()
    {
        var store = new InMemoryStore { CachedBytes = "cached"u8.ToArray(), WithMaintenance = true };
        CachedProxyPipeline pipeline = CreatePipeline(store, new StubUpstreamHandler(), evictionStrategy: EvictionStrategy.Lru, evictionEnabled: true);

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        CachedStreamRequest request = MakeStreamRequest(ttl: TimeSpan.FromMinutes(30), lruTouch: true);
        await pipeline.RunCachedStreamAsync(context, request, TestContext.Current.CancellationToken);

        Assert.True(store.TouchCalled, "TouchAsync should be called on LRU cache hit.");
        Assert.Single(store.TouchedKeys);
        Assert.Equal(CacheKey, store.TouchedKeys[0]);
    }

    [Fact]
    public async Task RunCachedStream_CacheHit_OldestStrategy_NoTouch()
    {
        var store = new InMemoryStore { CachedBytes = "cached"u8.ToArray(), WithMaintenance = true };
        CachedProxyPipeline pipeline = CreatePipeline(store, new StubUpstreamHandler(), evictionStrategy: EvictionStrategy.Oldest, evictionEnabled: true);

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        CachedStreamRequest request = MakeStreamRequest(ttl: TimeSpan.FromMinutes(30), lruTouch: true);
        await pipeline.RunCachedStreamAsync(context, request, TestContext.Current.CancellationToken);

        Assert.False(store.TouchCalled, "TouchAsync should not be called with Oldest strategy.");
    }

    [Fact]
    public async Task RunCachedStream_CacheHit_EvictionDisabled_NoTouch()
    {
        var store = new InMemoryStore { CachedBytes = "cached"u8.ToArray(), WithMaintenance = true };
        CachedProxyPipeline pipeline = CreatePipeline(store, new StubUpstreamHandler(), evictionStrategy: EvictionStrategy.Lru, evictionEnabled: false);

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        CachedStreamRequest request = MakeStreamRequest(ttl: TimeSpan.FromMinutes(30), lruTouch: true);
        await pipeline.RunCachedStreamAsync(context, request, TestContext.Current.CancellationToken);

        Assert.False(store.TouchCalled, "TouchAsync should not be called when eviction is disabled.");
    }

    [Fact]
    public async Task RunCachedStream_CacheHit_StoreNotMaintenance_NoTouch()
    {
        var store = new InMemoryStore { CachedBytes = "cached"u8.ToArray(), WithMaintenance = false };
        CachedProxyPipeline pipeline = CreatePipeline(store, new StubUpstreamHandler(), evictionStrategy: EvictionStrategy.Lru, evictionEnabled: true);

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        CachedStreamRequest request = MakeStreamRequest(ttl: TimeSpan.FromMinutes(30), lruTouch: true);
        await pipeline.RunCachedStreamAsync(context, request, TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    // ── LRU touch on cache hit after lock (double-check) ────────────────────

    [Fact]
    public async Task RunCachedStream_DoubleCheckAfterLock_LruTouch_CallsTouchAsync()
    {
        byte[] cachedBytes = "filled-after-lock"u8.ToArray();
        var store = new MaintenanceDelayedHitStore(cachedBytes, hitOnAttempt: 2);
        CachedProxyPipeline pipeline = CreatePipeline(store, new StubUpstreamHandler(), evictionStrategy: EvictionStrategy.Lru, evictionEnabled: true);

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        CachedStreamRequest request = MakeStreamRequest(ttl: TimeSpan.FromMinutes(30), lruTouch: true);
        await pipeline.RunCachedStreamAsync(context, request, TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(cachedBytes, ReadBody(context));
        Assert.True(store.TouchCalled, "TouchAsync should be called on LRU cache hit (after lock).");
        Assert.Single(store.TouchedKeys);
        Assert.Equal(CacheKey, store.TouchedKeys[0]);
    }

    // ── Negative cache ──────────────────────────────────────────────────────

    [Fact]
    public async Task RunCachedStream_NegativeCacheHit_Returns404()
    {
        var negCache = new NegativeCache(maxEntries: 100, TimeProvider.System);
        string negKey = ForwardKey + RemainingPath;
        negCache.Store(negKey, TimeSpan.FromMinutes(5));

        var store = new InMemoryStore { ReturnsNull = true };
        var handler = new StubUpstreamHandler();
        CachedProxyPipeline pipeline = CreatePipeline(store, handler, negativeCache: negCache);

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        CachedStreamRequest request = MakeStreamRequest(ttl: null, useNegativeCache: true, negCacheKey: negKey);
        await pipeline.RunCachedStreamAsync(context, request, TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Equal(0, handler.GetCount(s_upstreamUri.ToString()));
    }

    // ── Discovery failure ───────────────────────────────────────────────────

    [Fact]
    public async Task RunCachedStream_DiscoveryFailed_Returns502()
    {
        var store = new InMemoryStore { ReturnsNull = true };
        CachedProxyPipeline pipeline = CreatePipelineWithFailingDiscovery(store);

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        CachedStreamRequest request = MakeStreamRequestWithDiscovery(ttl: null);
        await pipeline.RunCachedStreamAsync(context, request, TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status502BadGateway, context.Response.StatusCode);
    }

    // ── Forward key not found ───────────────────────────────────────────────

    [Fact]
    public async Task RunCachedStream_ForwardKeyNotFound_WithStaleCache_ServesStaleFallback()
    {
        DateTimeOffset staleTime = DateTimeOffset.UtcNow - TimeSpan.FromHours(1);
        byte[] cachedBytes = "stale-but-usable"u8.ToArray();
        var store = new InMemoryStore { CachedBytes = cachedBytes, StoredAt = staleTime };
        CachedProxyPipeline pipeline = CreatePipelineWithEmptyForwardMap(store);

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        CachedStreamRequest request = MakeStreamRequestWithDiscovery(ttl: TimeSpan.FromMinutes(30), staleFallback: true);
        await pipeline.RunCachedStreamAsync(context, request, TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(cachedBytes, ReadBody(context));
    }

    [Fact]
    public async Task RunCachedStream_ForwardKeyNotFound_NoCache_Returns404()
    {
        var store = new InMemoryStore { ReturnsNull = true };
        CachedProxyPipeline pipeline = CreatePipelineWithEmptyForwardMap(store);

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        CachedStreamRequest request = MakeStreamRequestWithDiscovery(ttl: null);
        await pipeline.RunCachedStreamAsync(context, request, TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
    }

    // ── Upstream 304 revalidation ───────────────────────────────────────────

    [Fact]
    public async Task RunCachedStream_Upstream304_RevalidatesAndServesFromCache()
    {
        DateTimeOffset staleTime = DateTimeOffset.UtcNow - TimeSpan.FromHours(1);
        byte[] cachedBytes = "cached-with-etag"u8.ToArray();
        var store = new InMemoryStore
        {
            CachedBytes = cachedBytes,
            StoredAt = staleTime,
            ETag = "\"abc123\"",
            WithRevalidation = true,
        };
        var handler = new StubUpstreamHandler();
        handler.MapFactory(s_upstreamUri.ToString(), () => new HttpResponseMessage(HttpStatusCode.NotModified));
        CachedProxyPipeline pipeline = CreatePipeline(store, handler);

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        CachedStreamRequest request = MakeStreamRequest(ttl: TimeSpan.FromMinutes(30));
        await pipeline.RunCachedStreamAsync(context, request, TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(cachedBytes, ReadBody(context));
        Assert.True(store.RevalidationCalled, "RefreshTimestampAsync should be called on 304 Not Modified.");
    }

    // ── Upstream non-success ────────────────────────────────────────────────

    [Fact]
    public async Task RunCachedStream_UpstreamNonSuccess_WithStaleCache_ServesStaleFallback()
    {
        DateTimeOffset staleTime = DateTimeOffset.UtcNow - TimeSpan.FromHours(1);
        byte[] cachedBytes = "stale-fallback"u8.ToArray();
        var store = new InMemoryStore { CachedBytes = cachedBytes, StoredAt = staleTime };
        var handler = new StubUpstreamHandler();
        handler.MapStatus(s_upstreamUri.ToString(), HttpStatusCode.InternalServerError);
        CachedProxyPipeline pipeline = CreatePipeline(store, handler);

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        CachedStreamRequest request = MakeStreamRequest(ttl: TimeSpan.FromMinutes(30), staleFallback: true);
        await pipeline.RunCachedStreamAsync(context, request, TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(cachedBytes, ReadBody(context));
    }

    [Fact]
    public async Task RunCachedStream_UpstreamNonSuccess_NoCache_PassesThroughStatus()
    {
        var store = new InMemoryStore { ReturnsNull = true };
        var handler = new StubUpstreamHandler();
        handler.MapStatus(s_upstreamUri.ToString(), HttpStatusCode.ServiceUnavailable);
        CachedProxyPipeline pipeline = CreatePipeline(store, handler);

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        CachedStreamRequest request = MakeStreamRequest(ttl: null, staleFallback: true);
        await pipeline.RunCachedStreamAsync(context, request, TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
    }

    [Fact]
    public async Task RunCachedStream_Upstream404_StoresNegativeCacheEntry()
    {
        var negCache = new NegativeCache(maxEntries: 100, TimeProvider.System);
        string negKey = ForwardKey + RemainingPath;
        var store = new InMemoryStore { ReturnsNull = true };
        var handler = new StubUpstreamHandler();
        handler.MapStatus(s_upstreamUri.ToString(), HttpStatusCode.NotFound);
        CachedProxyPipeline pipeline = CreatePipeline(store, handler, negativeCache: negCache);

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        CachedStreamRequest request = MakeStreamRequest(ttl: null, useNegativeCache: true, negCacheKey: negKey);
        await pipeline.RunCachedStreamAsync(context, request, TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.True(negCache.TryGet(negKey), "404 should be stored in the negative cache.");
    }

    // ── Missing Content-Length ──────────────────────────────────────────────

    [Fact]
    public async Task RunCachedStream_MissingContentLength_WithStaleCache_ServesStaleFallback()
    {
        DateTimeOffset staleTime = DateTimeOffset.UtcNow - TimeSpan.FromHours(1);
        byte[] cachedBytes = "stale-no-cl"u8.ToArray();
        var store = new InMemoryStore { CachedBytes = cachedBytes, StoredAt = staleTime };
        var handler = new StubUpstreamHandler();
        handler.MapFactory(s_upstreamUri.ToString(), () => HttpResponseFactory.NoContentLength("upstream-no-cl"u8.ToArray(), "application/octet-stream"));
        CachedProxyPipeline pipeline = CreatePipeline(store, handler);

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        CachedStreamRequest request = MakeStreamRequest(ttl: TimeSpan.FromMinutes(30), staleFallback: true);
        await pipeline.RunCachedStreamAsync(context, request, TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(cachedBytes, ReadBody(context));
    }

    [Fact]
    public async Task RunCachedStream_MissingContentLength_NoCache_StreamsWithoutCaching()
    {
        var store = new InMemoryStore { ReturnsNull = true };
        var handler = new StubUpstreamHandler();
        handler.MapFactory(s_upstreamUri.ToString(), () => HttpResponseFactory.NoContentLength("streamed-live"u8.ToArray(), "application/octet-stream"));
        CachedProxyPipeline pipeline = CreatePipeline(store, handler);

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        CachedStreamRequest request = MakeStreamRequest(ttl: null);
        await pipeline.RunCachedStreamAsync(context, request, TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("streamed-live", ReadStringBody(context));
    }

    // ── Cache write failure ─────────────────────────────────────────────────

    [Fact]
    public async Task RunCachedStream_BeginWriteThrows_StreamsToClientWithoutCaching()
    {
        var store = new InMemoryStore { ReturnsNull = true, BeginWriteThrows = true };
        var handler = new StubUpstreamHandler();
        handler.MapBytes(s_upstreamUri.ToString(), s_bodyBytes, "application/octet-stream");
        CachedProxyPipeline pipeline = CreatePipeline(store, handler);

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        CachedStreamRequest request = MakeStreamRequest(ttl: null);
        await pipeline.RunCachedStreamAsync(context, request, TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(s_bodyBytes, ReadBody(context));
    }

    [Fact]
    public async Task RunCachedStream_CacheWriteFailureMidStream_StillServesClient()
    {
        var store = new InMemoryStore { ReturnsNull = true, WriteHandleThrowsAfterBytes = 4 };
        var handler = new StubUpstreamHandler();
        handler.MapBytes(s_upstreamUri.ToString(), s_bodyBytes, "application/octet-stream");
        CachedProxyPipeline pipeline = CreatePipeline(store, handler);

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        CachedStreamRequest request = MakeStreamRequest(ttl: null);
        await pipeline.RunCachedStreamAsync(context, request, TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(s_bodyBytes, ReadBody(context));
    }

    // ── Exception handling ──────────────────────────────────────────────────

    [Fact]
    public async Task RunCachedStream_HttpRequestException_WithStaleCache_ServesStaleFallback()
    {
        DateTimeOffset staleTime = DateTimeOffset.UtcNow - TimeSpan.FromHours(1);
        byte[] cachedBytes = "stale-on-error"u8.ToArray();
        var store = new InMemoryStore { CachedBytes = cachedBytes, StoredAt = staleTime };
        CachedProxyPipeline pipeline = CreatePipeline(store, new ThrowingHandler());

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        CachedStreamRequest request = MakeStreamRequest(ttl: TimeSpan.FromMinutes(30), staleFallback: true);
        await pipeline.RunCachedStreamAsync(context, request, TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(cachedBytes, ReadBody(context));
    }

    [Fact]
    public async Task RunCachedStream_HttpRequestException_NoCache_Returns502()
    {
        var store = new InMemoryStore { ReturnsNull = true };
        CachedProxyPipeline pipeline = CreatePipeline(store, new ThrowingHandler());

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        CachedStreamRequest request = MakeStreamRequest(ttl: null, staleFallback: true);
        await pipeline.RunCachedStreamAsync(context, request, TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status502BadGateway, context.Response.StatusCode);
    }

    [Fact]
    public async Task RunCachedStream_ExecutionRejectedException_WithStaleCache_ServesStaleFallback()
    {
        DateTimeOffset staleTime = DateTimeOffset.UtcNow - TimeSpan.FromHours(1);
        byte[] cachedBytes = "stale-on-reject"u8.ToArray();
        var store = new InMemoryStore { CachedBytes = cachedBytes, StoredAt = staleTime };
        CachedProxyPipeline pipeline = CreatePipeline(store, new RejectingHandler());

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        CachedStreamRequest request = MakeStreamRequest(ttl: TimeSpan.FromMinutes(30), staleFallback: true);
        await pipeline.RunCachedStreamAsync(context, request, TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(cachedBytes, ReadBody(context));
    }

    // ── Generic exception handling ──────────────────────────────────────────

    [Fact]
    public async Task RunCachedStream_GenericException_WithStaleCache_ServesStaleFallback()
    {
        DateTimeOffset staleTime = DateTimeOffset.UtcNow - TimeSpan.FromHours(1);
        byte[] cachedBytes = "stale-on-generic"u8.ToArray();
        var store = new InMemoryStore { CachedBytes = cachedBytes, StoredAt = staleTime };
        CachedProxyPipeline pipeline = CreatePipeline(store, new GenericThrowingHandler());

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        CachedStreamRequest request = MakeStreamRequest(ttl: TimeSpan.FromMinutes(30), staleFallback: true);
        await pipeline.RunCachedStreamAsync(context, request, TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(cachedBytes, ReadBody(context));
    }

    [Fact]
    public async Task RunCachedStream_GenericException_NoCache_Returns502()
    {
        var store = new InMemoryStore { ReturnsNull = true };
        CachedProxyPipeline pipeline = CreatePipeline(store, new GenericThrowingHandler());

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        CachedStreamRequest request = MakeStreamRequest(ttl: null, staleFallback: true);
        await pipeline.RunCachedStreamAsync(context, request, TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status502BadGateway, context.Response.StatusCode);
    }

    [Fact]
    public async Task RunCachedStream_ClientCancellation_DoesNotThrow()
    {
        var store = new InMemoryStore { ReturnsNull = true };
        var handler = new StubUpstreamHandler();
        handler.MapFactory(s_upstreamUri.ToString(), () => throw new HttpRequestException("Client cancelled", new TaskCanceledException()));
        CachedProxyPipeline pipeline = CreatePipeline(store, handler);

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        context.RequestAborted = CancellationTokenSource.CreateLinkedTokenSource(cts.Token).Token;

        // Should not throw — the cancellation is caught and recorded.
        await pipeline.RunCachedStreamAsync(context, MakeStreamRequest(ttl: null), CancellationToken.None);
    }

    [Fact]
    public async Task TouchCacheHit_TouchAsyncThrows_IsSwallowed()
    {
        byte[] cachedBytes = "cached"u8.ToArray();
        var store = new ThrowingTouchStore(cachedBytes);
        CachedProxyPipeline pipeline = CreatePipeline(store, new StubUpstreamHandler(), evictionStrategy: EvictionStrategy.Lru, evictionEnabled: true);

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        CachedStreamRequest request = MakeStreamRequest(ttl: TimeSpan.FromMinutes(30), lruTouch: true);
        await pipeline.RunCachedStreamAsync(context, request, TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(cachedBytes, ReadBody(context));
    }

    // ── HEAD request ──────────────────────────────────────────────────────────

    [Fact]
    public async Task RunCachedStream_HeadRequest_ValidHandle_ReturnsHeadersOnly()
    {
        var store = new InMemoryStore();
        var handler = new StubUpstreamHandler();
        handler.MapBytes(s_upstreamUri.ToString(), s_bodyBytes, "application/octet-stream");
        CachedProxyPipeline pipeline = CreatePipeline(store, handler);

        DefaultHttpContext context = TestContextFactory.MakeHeadContext();
        CachedStreamRequest request = MakeStreamRequest(ttl: null, supportsHead: true);
        await pipeline.RunCachedStreamAsync(context, request, TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Empty(ReadBody(context));
        Assert.Equal(1, store.WriteHandleDisposeCount);
    }

    [Fact]
    public async Task RunCachedStream_HeadRequest_NullHandle_ReturnsHeadersOnly()
    {
        var store = new InMemoryStore { BeginWriteThrows = true };
        var handler = new StubUpstreamHandler();
        handler.MapBytes(s_upstreamUri.ToString(), s_bodyBytes, "application/octet-stream");
        CachedProxyPipeline pipeline = CreatePipeline(store, handler);

        DefaultHttpContext context = TestContextFactory.MakeHeadContext();
        CachedStreamRequest request = MakeStreamRequest(ttl: null, supportsHead: true);
        await pipeline.RunCachedStreamAsync(context, request, TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Empty(ReadBody(context));
    }

    // ── ETag echo ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunCachedStream_Upstream200_WithEtag_ValidHandle_SetsETagHeader()
    {
        var store = new InMemoryStore();
        var handler = new StubUpstreamHandler();
        handler.MapFactory(s_upstreamUri.ToString(), () =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(s_bodyBytes)
                {
                    Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") },
                },
            };
            response.Headers.ETag = new EntityTagHeaderValue("\"test-etag\"");
            return response;
        });
        CachedProxyPipeline pipeline = CreatePipeline(store, handler);

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        CachedStreamRequest request = MakeStreamRequest(ttl: null);
        await pipeline.RunCachedStreamAsync(context, request, TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("\"test-etag\"", context.Response.Headers.ETag);
    }

    [Fact]
    public async Task RunCachedStream_Upstream200_WithEtag_NullHandle_SetsETagHeader()
    {
        var store = new InMemoryStore { BeginWriteThrows = true };
        var handler = new StubUpstreamHandler();
        handler.MapFactory(s_upstreamUri.ToString(), () =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(s_bodyBytes)
                {
                    Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") },
                },
            };
            response.Headers.ETag = new EntityTagHeaderValue("\"test-etag\"");
            return response;
        });
        CachedProxyPipeline pipeline = CreatePipeline(store, handler);

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        CachedStreamRequest request = MakeStreamRequest(ttl: null);
        await pipeline.RunCachedStreamAsync(context, request, TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("\"test-etag\"", context.Response.Headers.ETag);
    }

    // ── Content-Type fallback ─────────────────────────────────────────────────

    [Fact]
    public async Task RunCachedStream_Upstream200_NoContentType_UsesDefaultContentType()
    {
        var store = new InMemoryStore();
        var handler = new StubUpstreamHandler();
        handler.MapFactory(s_upstreamUri.ToString(), () =>
        {
            // ByteArrayContent has Content-Length but no Content-Type
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(s_bodyBytes),
            };
        });
        CachedProxyPipeline pipeline = CreatePipeline(store, handler);

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        CachedStreamRequest request = MakeStreamRequest(ttl: null);
        await pipeline.RunCachedStreamAsync(context, request, TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("application/octet-stream", context.Response.ContentType);
    }

    // ── Short upstream read (copied != contentLength) ─────────────────────────

    [Fact]
    public async Task RunCachedStream_UpstreamShortRead_SkipsCommit()
    {
        var store = new InMemoryStore();
        var handler = new StubUpstreamHandler();
        handler.MapFactory(s_upstreamUri.ToString(), () =>
            HttpResponseFactory.ShortRead(s_bodyBytes, s_bodyBytes.Length * 2, "application/octet-stream"));
        CachedProxyPipeline pipeline = CreatePipeline(store, handler);

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        CachedStreamRequest request = MakeStreamRequest(ttl: null);
        await pipeline.RunCachedStreamAsync(context, request, TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(s_bodyBytes, ReadBody(context));
        Assert.False(store.WasWritten, "CommitAsync should not be called when copied bytes differ from Content-Length.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static CachedStreamRequest MakeStreamRequest(TimeSpan? ttl, bool lruTouch = false, bool staleFallback = false, bool useNegativeCache = false, string? negCacheKey = null, bool supportsHead = false)
    {
        return new CachedStreamRequest
        {
            CacheKey = CacheKey,
            ForwardKey = ForwardKey,
            StageLabel = "test",
            UpstreamUri = s_upstreamUri,
            BuildUpstreamUri = (_, base_) => new Uri(base_ + RemainingPath),
            Telemetry = new ProxyFeatureTelemetry(),
            DefaultContentType = "application/octet-stream",
            SupportsHead = supportsHead,
            LruTouch = lruTouch,
            Ttl = ttl,
            StaleFallbackEnabled = staleFallback,
            UseNegativeCache = useNegativeCache,
            NegCacheKey = negCacheKey,
            CacheContentType = "test",
        };
    }

    private static CachedStreamRequest MakeStreamRequestWithDiscovery(TimeSpan? ttl, bool staleFallback = false)
    {
        return new CachedStreamRequest
        {
            CacheKey = CacheKey,
            ForwardKey = ForwardKey,
            StageLabel = "test",
            BuildUpstreamUri = (_, base_) => new Uri(base_ + RemainingPath),
            Telemetry = new ProxyFeatureTelemetry(),
            DefaultContentType = "application/octet-stream",
            Ttl = ttl,
            StaleFallbackEnabled = staleFallback,
            CacheContentType = "test",
        };
    }

    private static byte[] ReadBody(DefaultHttpContext context)
    {
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var ms = new MemoryStream();
        context.Response.Body.CopyTo(ms);
        return ms.ToArray();
    }

    private static string ReadStringBody(DefaultHttpContext context)
    {
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8, leaveOpen: true);
        return reader.ReadToEnd();
    }

    private static CachedProxyPipeline CreatePipeline(
        IPackageContentStore store,
        HttpMessageHandler handler,
        EvictionStrategy evictionStrategy = EvictionStrategy.Oldest,
        bool evictionEnabled = false,
        NegativeCache? negativeCache = null)
    {
        var options = new MirrorOptions
        {
            Cache = new CacheOptions
            {
                Enabled = true,
                Eviction = new CacheEvictionOptions
                {
                    Enabled = evictionEnabled,
                    Strategy = evictionStrategy,
                },
                NegativeCache = new NegativeCacheOptions { Enabled = true, Ttl = TimeSpan.FromMinutes(5) },
            },
        };
        return CreatePipeline(store, handler, options, negativeCache);
    }

    [Fact]
    public async Task IsCacheFresh_RespectsTtl()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var handler = new CountingHandler();
        var options = new MirrorOptions { Cache = { Enabled = true } };
        var store = new InMemoryStore { CachedBytes = s_bodyBytes, StoredAt = clock.GetUtcNow() };
        CachedProxyPipeline pipeline = CreatePipeline(store, handler, options, negativeCache: null, clock);

        // Request with 30-min TTL — should serve from cache
        CachedStreamRequest request = MakeStreamRequest(ttl: TimeSpan.FromMinutes(30));

        var context = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
        await pipeline.RunCachedStreamAsync(context, request, TestContext.Current.CancellationToken);
        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal(0, handler.CallCount); // served from cache, no upstream call

        // Advance clock past TTL — entry is now stale; should fetch from upstream
        clock.Advance(TimeSpan.FromMinutes(31));

        context = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
        await pipeline.RunCachedStreamAsync(context, request, TestContext.Current.CancellationToken);
        Assert.Equal(1, handler.CallCount); // upstream call made
    }

    private static CachedProxyPipeline CreatePipeline(
        IPackageContentStore store,
        HttpMessageHandler handler,
        MirrorOptions options,
        NegativeCache? negativeCache,
        TimeProvider? timeProvider = null)
    {
        TimeProvider tp = timeProvider ?? TimeProvider.System;
        IOptions<MirrorOptions> optionsWrapper = Options.Create(options);
        var httpClientFactory = new TestHttpClientFactory(handler);
        var upstreamClient = new UpstreamClient(httpClientFactory, optionsWrapper, NullLogger<UpstreamClient>.Instance, new MirrorMetrics());
        var discovery = new DiscoveryCache(upstreamClient, optionsWrapper, NullLogger<DiscoveryCache>.Instance, new MirrorMetrics(), tp);
        DiscoveryCacheSeeder.SeedSnapshot(discovery, new Dictionary<string, string> { [ForwardKey] = UpstreamBase });
        var keyedLock = new KeyedAsyncLock(NullLogger<KeyedAsyncLock>.Instance);
        return new CachedProxyPipeline(upstreamClient, discovery, store, keyedLock, optionsWrapper, new MirrorMetrics(), NullLogger.Instance, tp, negativeCache);
    }

    private static CachedProxyPipeline CreatePipelineWithFailingDiscovery(IPackageContentStore store)
    {
        var options = new MirrorOptions
        {
            Upstream = { DiscoveryCacheTtl = TimeSpan.Zero },
            Cache = { Enabled = true },
        };
        IOptions<MirrorOptions> optionsWrapper = Options.Create(options);
        var httpClientFactory = new TestHttpClientFactory(new ThrowingHandler());
        var upstreamClient = new UpstreamClient(httpClientFactory, optionsWrapper, NullLogger<UpstreamClient>.Instance, new MirrorMetrics());
        var discovery = new DiscoveryCache(upstreamClient, optionsWrapper, NullLogger<DiscoveryCache>.Instance, new MirrorMetrics(), TimeProvider.System);
        var keyedLock = new KeyedAsyncLock(NullLogger<KeyedAsyncLock>.Instance);
        return new CachedProxyPipeline(upstreamClient, discovery, store, keyedLock, optionsWrapper, new MirrorMetrics(), NullLogger.Instance, TimeProvider.System);
    }

    private static CachedProxyPipeline CreatePipelineWithEmptyForwardMap(IPackageContentStore store)
    {
        var options = new MirrorOptions { Cache = { Enabled = true } };
        IOptions<MirrorOptions> optionsWrapper = Options.Create(options);
        var handler = new StubUpstreamHandler();
        var httpClientFactory = new TestHttpClientFactory(handler);
        var upstreamClient = new UpstreamClient(httpClientFactory, optionsWrapper, NullLogger<UpstreamClient>.Instance, new MirrorMetrics());
        var discovery = new DiscoveryCache(upstreamClient, optionsWrapper, NullLogger<DiscoveryCache>.Instance, new MirrorMetrics(), TimeProvider.System);
        DiscoveryCacheSeeder.SeedSnapshot(discovery, []);
        var keyedLock = new KeyedAsyncLock(NullLogger<KeyedAsyncLock>.Instance);
        return new CachedProxyPipeline(upstreamClient, discovery, store, keyedLock, optionsWrapper, new MirrorMetrics(), NullLogger.Instance, TimeProvider.System);
    }

    // ── Inner test doubles ───────────────────────────────────────────────────

    private sealed class CountingHandler : HttpMessageHandler
    {
        private int _callCount;
        public int CallCount => _callCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(s_bodyBytes),
            });
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("Simulated upstream failure");
    }

    private sealed class RejectingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new TestExecutionRejectedException("Simulated Polly rejection");
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1032:Implement standard exception constructors", Justification = "Test-only exception; only message constructor is needed.")]
    private sealed class TestExecutionRejectedException(string message) : ExecutionRejectedException(message);

    private sealed class ThrowingTouchStore(byte[] cachedBytes) : IPackageContentStore, ICacheMaintenance
    {
        public ValueTask<CachedContent?> TryGetAsync(string key, CancellationToken ct)
        {
            return ValueTask.FromResult<CachedContent?>(new CachedContent
            {
                Stream = new MemoryStream(cachedBytes),
                Length = cachedBytes.Length,
                ContentType = "application/octet-stream",
                StoredAtUtc = DateTimeOffset.UtcNow,
            });
        }

        public ValueTask<ICacheWriteHandle> BeginWriteAsync(string key, CancellationToken ct)
            => throw new NotSupportedException();

        public async IAsyncEnumerable<CacheEntryInfo> EnumerateAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DeleteAsync(string key, CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask TouchAsync(string key, CancellationToken ct)
            => throw new InvalidOperationException("Simulated touch failure");
    }

    private sealed class MaintenanceDelayedHitStore(byte[] cachedBytes, int hitOnAttempt) : IPackageContentStore, ICacheMaintenance
    {
        private int _attempt;

        public bool TouchCalled { get; private set; }
        public List<string> TouchedKeys { get; } = [];

        public ValueTask<CachedContent?> TryGetAsync(string key, CancellationToken ct)
        {
            _attempt++;
            if (_attempt >= hitOnAttempt)
            {
                return ValueTask.FromResult<CachedContent?>(new CachedContent
                {
                    Stream = new MemoryStream(cachedBytes),
                    Length = cachedBytes.Length,
                    ContentType = "application/octet-stream",
                    StoredAtUtc = DateTimeOffset.UtcNow,
                });
            }

            return ValueTask.FromResult<CachedContent?>(null);
        }

        public ValueTask<ICacheWriteHandle> BeginWriteAsync(string key, CancellationToken ct)
            => throw new NotSupportedException();

        public async IAsyncEnumerable<CacheEntryInfo> EnumerateAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DeleteAsync(string key, CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask TouchAsync(string key, CancellationToken ct)
        {
            TouchCalled = true;
            TouchedKeys.Add(key);
            return ValueTask.CompletedTask;
        }
    }
}
