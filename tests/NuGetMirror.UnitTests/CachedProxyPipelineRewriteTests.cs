using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NuGetMirror.Configuration;
using NuGetMirror.Diagnostics;
using NuGetMirror.Discovery;
using NuGetMirror.Proxy;
using NuGetMirror.Storage;
using NuGetMirror.TestKit;
using NuGetMirror.Upstream;

using Polly;

namespace NuGetMirror.UnitTests;

public sealed class CachedProxyPipelineRewriteTests
{
    private const string CacheKey = "$registration/semver2/newtonsoft.json/index.json";
    private const string ForwardKey = "/v3/registration-semver2/";
    private const string UpstreamBase = "https://api.nuget.org/v3/registration5-gz-semver2/";
    private const string RemainingPath = "newtonsoft.json/index.json";
    private const string UpstreamJson = """{"items":[{"packageContent":"https://api.nuget.org/v3-flatcontainer/newtonsoft.json/13.0.3/newtonsoft.json.13.0.3.nupkg"}]}""";

    // ── Cache hit (fresh) ───────────────────────────────────────────────────

    [Fact]
    public async Task RunCachedRewriteIndex_CacheHit_Fresh_ServesWithRewriting()
    {
        byte[] bodyBytes = Encoding.UTF8.GetBytes(UpstreamJson);
        var store = new RewriteStore { CachedBytes = bodyBytes };
        CachedProxyPipeline pipeline = CreatePipeline(store, new StubUpstreamHandler(), publicBaseUrl: "http://localhost");

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        await pipeline.RunCachedRewriteIndexAsync(context, MakeRewriteRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        string body = ReadBody(context);
        Assert.Contains("http://localhost/v3-flatcontainer/", body, StringComparison.Ordinal);
        Assert.DoesNotContain("api.nuget.org", body, StringComparison.Ordinal);
    }

    // ── Cache hit after lock (double-check) ─────────────────────────────────

    [Fact]
    public async Task RunCachedRewriteIndex_DoubleCheckAfterLock_ServesFromCache()
    {
        byte[] bodyBytes = Encoding.UTF8.GetBytes(UpstreamJson);
        var store = new DelayedRewriteStore(bodyBytes, hitOnAttempt: 2);
        CachedProxyPipeline pipeline = CreatePipeline(store, new StubUpstreamHandler(), publicBaseUrl: "http://localhost");

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        await pipeline.RunCachedRewriteIndexAsync(context, MakeRewriteRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        string body = ReadBody(context);
        Assert.Contains("http://localhost/v3-flatcontainer/", body, StringComparison.Ordinal);
        Assert.DoesNotContain("api.nuget.org", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunCachedRewriteIndex_DoubleCheckAfterLock_LruTouch_CallsTouchAsync()
    {
        byte[] bodyBytes = Encoding.UTF8.GetBytes(UpstreamJson);
        var store = new DelayedRewriteStore(bodyBytes, hitOnAttempt: 2);
        CachedProxyPipeline pipeline = CreatePipeline(store, new StubUpstreamHandler(), evictionStrategy: EvictionStrategy.Lru, evictionEnabled: true);

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        CachedRewriteIndexRequest request = MakeRewriteRequest(lruTouch: true);
        await pipeline.RunCachedRewriteIndexAsync(context, request, TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.True(store.TouchCalled, "TouchAsync should be called on LRU cache hit (after lock).");
        Assert.Single(store.TouchedKeys);
        Assert.Equal(CacheKey, store.TouchedKeys[0]);
    }

    [Fact]
    public async Task RunCachedRewriteIndex_CacheHit_LruTouch_CallsTouchAsync()
    {
        byte[] bodyBytes = Encoding.UTF8.GetBytes(UpstreamJson);
        var store = new RewriteStore { CachedBytes = bodyBytes, WithMaintenance = true };
        CachedProxyPipeline pipeline = CreatePipeline(store, new StubUpstreamHandler(), evictionStrategy: EvictionStrategy.Lru, evictionEnabled: true);

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        CachedRewriteIndexRequest request = MakeRewriteRequest(lruTouch: true);
        await pipeline.RunCachedRewriteIndexAsync(context, request, TestContext.Current.CancellationToken);

        Assert.True(store.TouchCalled, "TouchAsync should be called on LRU cache hit.");
        Assert.Single(store.TouchedKeys);
        Assert.Equal(CacheKey, store.TouchedKeys[0]);
    }

    // ── Cache hit (stale) → upstream fetch ──────────────────────────────────

    [Fact]
    public async Task RunCachedRewriteIndex_CacheHit_StaleBeyondTtl_FetchesFromUpstream()
    {
        DateTimeOffset staleTime = DateTimeOffset.UtcNow - TimeSpan.FromHours(1);
        var store = new RewriteStore { CachedBytes = Encoding.UTF8.GetBytes("{}"), StoredAt = staleTime };
        var handler = new StubUpstreamHandler();
        handler.MapJson(UpstreamBase + RemainingPath, UpstreamJson);
        CachedProxyPipeline pipeline = CreatePipeline(store, handler, publicBaseUrl: "http://localhost");

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        await pipeline.RunCachedRewriteIndexAsync(context, MakeRewriteRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        string body = ReadBody(context);
        Assert.Contains("http://localhost/v3-flatcontainer/", body, StringComparison.Ordinal);
        Assert.True(store.WasWritten, "Upstream response should be written to cache.");
    }

    // ── Invalid cache entry length ──────────────────────────────────────────

    [Fact]
    public async Task RunCachedRewriteIndex_CacheHit_InvalidLengthNegative_Returns500()
    {
        var store = new RewriteStore { CachedBytes = [], Length = -1 };
        CachedProxyPipeline pipeline = CreatePipeline(store, new StubUpstreamHandler());

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        await pipeline.RunCachedRewriteIndexAsync(context, MakeRewriteRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
    }

    [Fact]
    public async Task RunCachedRewriteIndex_CacheHit_InvalidLengthTooLarge_Returns500()
    {
        var store = new RewriteStore { CachedBytes = [], Length = (long)int.MaxValue + 1 };
        CachedProxyPipeline pipeline = CreatePipeline(store, new StubUpstreamHandler());

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        await pipeline.RunCachedRewriteIndexAsync(context, MakeRewriteRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
    }

    // ── Upstream 304 revalidation ───────────────────────────────────────────

    [Fact]
    public async Task RunCachedRewriteIndex_Upstream304_RevalidatesTimestampAndServes()
    {
        DateTimeOffset staleTime = DateTimeOffset.UtcNow - TimeSpan.FromHours(1);
        byte[] bodyBytes = Encoding.UTF8.GetBytes(UpstreamJson);
        var store = new RewriteStore
        {
            CachedBytes = bodyBytes,
            StoredAt = staleTime,
            ETag = "\"etag123\"",
            WithRevalidation = true,
        };
        var handler = new StubUpstreamHandler();
        handler.MapFactory(UpstreamBase + RemainingPath, () => new HttpResponseMessage(HttpStatusCode.NotModified));
        CachedProxyPipeline pipeline = CreatePipeline(store, handler, publicBaseUrl: "http://localhost");

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        await pipeline.RunCachedRewriteIndexAsync(context, MakeRewriteRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.True(store.RevalidationCalled, "RefreshTimestampAsync should be called on 304.");
    }

    // ── Body-size limit ─────────────────────────────────────────────────────

    [Fact]
    public async Task RunCachedRewriteIndex_BodyExceedsMaxBodyBytes_Returns502()
    {
        string largeJson = new('x', 200);
        var handler = new StubUpstreamHandler();
        handler.MapJson(UpstreamBase + RemainingPath, largeJson);
        var store = new RewriteStore { ReturnsNull = true };
        CachedProxyPipeline pipeline = CreatePipeline(store, handler);

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        CachedRewriteIndexRequest request = MakeRewriteRequest(maxBodyBytes: 10);
        await pipeline.RunCachedRewriteIndexAsync(context, request, TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status502BadGateway, context.Response.StatusCode);
    }

    // ── Upstream non-success ────────────────────────────────────────────────

    [Fact]
    public async Task RunCachedRewriteIndex_UpstreamNonSuccess_WithCached_ServesStale()
    {
        DateTimeOffset staleTime = DateTimeOffset.UtcNow - TimeSpan.FromHours(1);
        byte[] bodyBytes = Encoding.UTF8.GetBytes(UpstreamJson);
        var store = new RewriteStore { CachedBytes = bodyBytes, StoredAt = staleTime };
        var handler = new StubUpstreamHandler();
        handler.MapStatus(UpstreamBase + RemainingPath, HttpStatusCode.InternalServerError);
        CachedProxyPipeline pipeline = CreatePipeline(store, handler, publicBaseUrl: "http://localhost");

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        await pipeline.RunCachedRewriteIndexAsync(context, MakeRewriteRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        string body = ReadBody(context);
        Assert.Contains("http://localhost/v3-flatcontainer/", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunCachedRewriteIndex_UpstreamNonSuccess_NoCache_PassesStatus()
    {
        var handler = new StubUpstreamHandler();
        handler.MapStatus(UpstreamBase + RemainingPath, HttpStatusCode.ServiceUnavailable);
        var store = new RewriteStore { ReturnsNull = true };
        CachedProxyPipeline pipeline = CreatePipeline(store, handler);

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        await pipeline.RunCachedRewriteIndexAsync(context, MakeRewriteRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
    }

    // ── Forward key not found ───────────────────────────────────────────────

    [Fact]
    public async Task RunCachedRewriteIndex_ForwardKeyNotFound_WithCached_ServesStale()
    {
        DateTimeOffset staleTime = DateTimeOffset.UtcNow - TimeSpan.FromHours(1);
        byte[] bodyBytes = Encoding.UTF8.GetBytes(UpstreamJson);
        var store = new RewriteStore { CachedBytes = bodyBytes, StoredAt = staleTime };
        CachedProxyPipeline pipeline = CreatePipelineWithEmptyForwardMap(store, publicBaseUrl: "http://localhost");

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        await pipeline.RunCachedRewriteIndexAsync(context, MakeRewriteRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task RunCachedRewriteIndex_ForwardKeyNotFound_NoCache_Returns502()
    {
        var store = new RewriteStore { ReturnsNull = true };
        CachedProxyPipeline pipeline = CreatePipelineWithEmptyForwardMap(store);

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        await pipeline.RunCachedRewriteIndexAsync(context, MakeRewriteRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status502BadGateway, context.Response.StatusCode);
    }

    // ── Exception handling ──────────────────────────────────────────────────

    [Fact]
    public async Task RunCachedRewriteIndex_HttpRequestException_WithStaleCache_ServesStale()
    {
        DateTimeOffset staleTime = DateTimeOffset.UtcNow - TimeSpan.FromHours(1);
        byte[] bodyBytes = Encoding.UTF8.GetBytes(UpstreamJson);
        var store = new RewriteStore { CachedBytes = bodyBytes, StoredAt = staleTime };
        CachedProxyPipeline pipeline = CreatePipeline(store, new ThrowingHandler(), publicBaseUrl: "http://localhost");

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        await pipeline.RunCachedRewriteIndexAsync(context, MakeRewriteRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task RunCachedRewriteIndex_ExecutionRejectedException_WithStaleCache_ServesStale()
    {
        DateTimeOffset staleTime = DateTimeOffset.UtcNow - TimeSpan.FromHours(1);
        byte[] bodyBytes = Encoding.UTF8.GetBytes(UpstreamJson);
        var store = new RewriteStore { CachedBytes = bodyBytes, StoredAt = staleTime };
        CachedProxyPipeline pipeline = CreatePipeline(store, new RejectingHandler(), publicBaseUrl: "http://localhost");

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        await pipeline.RunCachedRewriteIndexAsync(context, MakeRewriteRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task RunCachedRewriteIndex_HttpRequestException_NoCache_Returns502()
    {
        var store = new RewriteStore { ReturnsNull = true };
        CachedProxyPipeline pipeline = CreatePipeline(store, new ThrowingHandler());

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        await pipeline.RunCachedRewriteIndexAsync(context, MakeRewriteRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status502BadGateway, context.Response.StatusCode);
    }

    [Fact]
    public async Task RunCachedRewriteIndex_GenericException_WithStaleCache_ServesStale()
    {
        DateTimeOffset staleTime = DateTimeOffset.UtcNow - TimeSpan.FromHours(1);
        byte[] bodyBytes = Encoding.UTF8.GetBytes(UpstreamJson);
        var store = new RewriteStore { CachedBytes = bodyBytes, StoredAt = staleTime };
        CachedProxyPipeline pipeline = CreatePipeline(store, new GenericThrowingHandler(), publicBaseUrl: "http://localhost");

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        await pipeline.RunCachedRewriteIndexAsync(context, MakeRewriteRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        string body = ReadBody(context);
        Assert.Contains("http://localhost/v3-flatcontainer/", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunCachedRewriteIndex_GenericException_NoCache_Returns502()
    {
        var store = new RewriteStore { ReturnsNull = true };
        CachedProxyPipeline pipeline = CreatePipeline(store, new GenericThrowingHandler());

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        await pipeline.RunCachedRewriteIndexAsync(context, MakeRewriteRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status502BadGateway, context.Response.StatusCode);
    }

    [Fact]
    public async Task RunCachedRewriteIndex_ClientCancellation_DoesNotThrow()
    {
        var store = new RewriteStore { ReturnsNull = true };
        var handler = new StubUpstreamHandler();
        handler.MapFactory(UpstreamBase + RemainingPath, () => throw new HttpRequestException("Client cancelled", new TaskCanceledException()));
        CachedProxyPipeline pipeline = CreatePipeline(store, handler);

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        context.RequestAborted = CancellationTokenSource.CreateLinkedTokenSource(cts.Token).Token;

        // Should not throw — the cancellation is caught and recorded.
        await pipeline.RunCachedRewriteIndexAsync(context, MakeRewriteRequest(), CancellationToken.None);
    }

    // ── Discovery failure ────────────────────────────────────────────────────

    [Fact]
    public async Task RunCachedRewriteIndex_DiscoveryFailed_WithStaleCache_ServesStale()
    {
        DateTimeOffset staleTime = DateTimeOffset.UtcNow - TimeSpan.FromHours(1);
        byte[] bodyBytes = Encoding.UTF8.GetBytes(UpstreamJson);
        var store = new RewriteStore { CachedBytes = bodyBytes, StoredAt = staleTime };
        CachedProxyPipeline pipeline = CreatePipelineWithFailingDiscovery(store, publicBaseUrl: "http://localhost");

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        await pipeline.RunCachedRewriteIndexAsync(context, MakeRewriteRequest(), TestContext.Current.CancellationToken);

        // Discovery failure with stale cache → HandleRewriteIndexErrorAsync → stale fallback
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        string body = ReadBody(context);
        Assert.Contains("http://localhost/v3-flatcontainer/", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunCachedRewriteIndex_DiscoveryFailed_NoCache_Returns502()
    {
        var store = new RewriteStore { ReturnsNull = true };
        CachedProxyPipeline pipeline = CreatePipelineWithFailingDiscovery(store);

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        await pipeline.RunCachedRewriteIndexAsync(context, MakeRewriteRequest(), TestContext.Current.CancellationToken);

        // Discovery failure with no cache → HandleRewriteIndexErrorAsync → 502
        Assert.Equal(StatusCodes.Status502BadGateway, context.Response.StatusCode);
    }

    // ── PublicBaseUrl affects ETag behavior ─────────────────────────────────

    [Fact]
    public async Task RunCachedRewriteIndex_CacheHit_WithPublicBaseUrl_SetsETagHeader()
    {
        byte[] bodyBytes = Encoding.UTF8.GetBytes(UpstreamJson);
        var store = new RewriteStore { CachedBytes = bodyBytes, ETag = "\"etag123\"" };
        CachedProxyPipeline pipeline = CreatePipeline(store, new StubUpstreamHandler(), publicBaseUrl: "http://localhost");

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        await pipeline.RunCachedRewriteIndexAsync(context, MakeRewriteRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("\"etag123\"", context.Response.Headers.ETag);
    }

    [Fact]
    public async Task RunCachedRewriteIndex_CacheHit_WithoutPublicBaseUrl_DoesNotSetETag()
    {
        byte[] bodyBytes = Encoding.UTF8.GetBytes(UpstreamJson);
        var store = new RewriteStore { CachedBytes = bodyBytes, ETag = "\"etag123\"" };
        CachedProxyPipeline pipeline = CreatePipeline(store, new StubUpstreamHandler(), publicBaseUrl: null);

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        await pipeline.RunCachedRewriteIndexAsync(context, MakeRewriteRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.True(string.IsNullOrEmpty(context.Response.Headers.ETag), "ETag should not be set when PublicBaseUrl is not configured.");
    }

    [Fact]
    public async Task RunCachedRewriteIndex_CacheHit_WithIfNoneMatch_Returns304()
    {
        byte[] bodyBytes = Encoding.UTF8.GetBytes(UpstreamJson);
        var store = new RewriteStore { CachedBytes = bodyBytes, ETag = "\"etag123\"" };
        CachedProxyPipeline pipeline = CreatePipeline(store, new StubUpstreamHandler(), publicBaseUrl: "http://localhost");

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        context.Request.Headers.IfNoneMatch = "\"etag123\"";
        await pipeline.RunCachedRewriteIndexAsync(context, MakeRewriteRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status304NotModified, context.Response.StatusCode);
    }

    // ── ETag from upstream ──────────────────────────────────────────────────

    [Fact]
    public async Task RunCachedRewriteIndex_Upstream200_WithEtag_PersistsEtagInCache()
    {
        var handler = new StubUpstreamHandler();
        handler.MapFactory(UpstreamBase + RemainingPath, () =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(UpstreamJson, Encoding.UTF8, "application/json"),
            };
            response.Headers.ETag = new EntityTagHeaderValue("\"upstream-etag\"");
            return response;
        });
        var store = new RewriteStore { ReturnsNull = true };
        CachedProxyPipeline pipeline = CreatePipeline(store, handler, publicBaseUrl: "http://localhost");

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        await pipeline.RunCachedRewriteIndexAsync(context, MakeRewriteRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.True(store.WasWritten, "Upstream response should be written to cache.");
        Assert.Equal("\"upstream-etag\"", store.WrittenEtag);
    }

    // ── Telemetry callbacks ─────────────────────────────────────────────────

    [Fact]
    public async Task RunCachedRewriteIndex_Upstream304_CallsOnRevalidated()
    {
        DateTimeOffset staleTime = DateTimeOffset.UtcNow - TimeSpan.FromHours(1);
        byte[] bodyBytes = Encoding.UTF8.GetBytes(UpstreamJson);
        bool? onRevalidated = null;
        var telemetry = new ProxyFeatureTelemetry(OnRevalidated: () => onRevalidated = true);
        var store = new RewriteStore
        {
            CachedBytes = bodyBytes,
            StoredAt = staleTime,
            ETag = "\"etag123\"",
            WithRevalidation = true,
        };
        var handler = new StubUpstreamHandler();
        handler.MapFactory(UpstreamBase + RemainingPath, () => new HttpResponseMessage(HttpStatusCode.NotModified));
        CachedProxyPipeline pipeline = CreatePipeline(store, handler, publicBaseUrl: "http://localhost");

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        await pipeline.RunCachedRewriteIndexAsync(context, MakeRewriteRequest(telemetry: telemetry), TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.True(onRevalidated, "OnRevalidated should be invoked on 304.");
    }

    [Fact]
    public async Task RunCachedRewriteIndex_UpstreamNonSuccess_WithCached_CallsOnStaleFallback()
    {
        DateTimeOffset staleTime = DateTimeOffset.UtcNow - TimeSpan.FromHours(1);
        byte[] bodyBytes = Encoding.UTF8.GetBytes(UpstreamJson);
        bool? onStaleFallback = null;
        bool? onCacheWriteSkipped = null;
        var telemetry = new ProxyFeatureTelemetry(
            OnStaleFallback: () => onStaleFallback = true,
            OnCacheWriteSkipped: () => onCacheWriteSkipped = true);
        var store = new RewriteStore { CachedBytes = bodyBytes, StoredAt = staleTime };
        var handler = new StubUpstreamHandler();
        handler.MapStatus(UpstreamBase + RemainingPath, HttpStatusCode.InternalServerError);
        CachedProxyPipeline pipeline = CreatePipeline(store, handler, publicBaseUrl: "http://localhost");

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        await pipeline.RunCachedRewriteIndexAsync(context, MakeRewriteRequest(telemetry: telemetry), TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.True(onStaleFallback, "OnStaleFallback should be invoked on upstream non-success with stale cache.");
        Assert.True(onCacheWriteSkipped, "OnCacheWriteSkipped should be invoked on upstream non-success.");
    }

    [Fact]
    public async Task RunCachedRewriteIndex_UpstreamNonSuccess_NoCache_CallsOnCacheWriteSkipped()
    {
        bool? onCacheWriteSkipped = null;
        var telemetry = new ProxyFeatureTelemetry(OnCacheWriteSkipped: () => onCacheWriteSkipped = true);
        var handler = new StubUpstreamHandler();
        handler.MapStatus(UpstreamBase + RemainingPath, HttpStatusCode.ServiceUnavailable);
        var store = new RewriteStore { ReturnsNull = true };
        CachedProxyPipeline pipeline = CreatePipeline(store, handler);

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        await pipeline.RunCachedRewriteIndexAsync(context, MakeRewriteRequest(telemetry: telemetry), TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.True(onCacheWriteSkipped, "OnCacheWriteSkipped should be invoked on upstream non-success.");
    }

    [Fact]
    public async Task RunCachedRewriteIndex_ForwardKeyNotFound_WithCache_CallsOnStaleFallback()
    {
        DateTimeOffset staleTime = DateTimeOffset.UtcNow - TimeSpan.FromHours(1);
        byte[] bodyBytes = Encoding.UTF8.GetBytes(UpstreamJson);
        bool? onStaleFallback = null;
        var telemetry = new ProxyFeatureTelemetry(OnStaleFallback: () => onStaleFallback = true);
        var store = new RewriteStore { CachedBytes = bodyBytes, StoredAt = staleTime };
        CachedProxyPipeline pipeline = CreatePipelineWithEmptyForwardMap(store, publicBaseUrl: "http://localhost");

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        await pipeline.RunCachedRewriteIndexAsync(context, MakeRewriteRequest(telemetry: telemetry), TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.True(onStaleFallback, "OnStaleFallback should be invoked on forward-key miss with stale cache.");
    }

    // ── Cache write failure ───────────────────────────────────────────────────

    [Fact]
    public async Task RunCachedRewriteIndex_BeginWriteThrows_StillServesRewrittenBody()
    {
        var handler = new StubUpstreamHandler();
        handler.MapJson(UpstreamBase + RemainingPath, UpstreamJson);
        var store = new InMemoryStore { BeginWriteThrows = true, ReturnsNull = true };
        CachedProxyPipeline pipeline = CreatePipeline(store, handler, publicBaseUrl: "http://localhost");

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        await pipeline.RunCachedRewriteIndexAsync(context, MakeRewriteRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        string body = ReadBody(context);
        Assert.Contains("http://localhost/v3-flatcontainer/", body, StringComparison.Ordinal);
    }

    // ── 304 without cached entry ──────────────────────────────────────────────

    [Fact]
    public async Task RunCachedRewriteIndex_Upstream304_NoCache_PassesThrough304()
    {
        var handler = new StubUpstreamHandler();
        handler.MapFactory(UpstreamBase + RemainingPath, () => new HttpResponseMessage(HttpStatusCode.NotModified));
        var store = new RewriteStore { ReturnsNull = true };
        CachedProxyPipeline pipeline = CreatePipeline(store, handler);

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        await pipeline.RunCachedRewriteIndexAsync(context, MakeRewriteRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status304NotModified, context.Response.StatusCode);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static CachedRewriteIndexRequest MakeRewriteRequest(long maxBodyBytes = 256 * 1024, bool lruTouch = false, ProxyFeatureTelemetry? telemetry = null)
    {
        return new CachedRewriteIndexRequest
        {
            CacheKey = CacheKey,
            ForwardKey = ForwardKey,
            StageLabel = "registration",
            BuildUpstreamUri = (_, base_) => new Uri(base_ + RemainingPath),
            RewriteBody = (body, mirrorBase) => body.Replace("https://api.nuget.org/v3-flatcontainer/", mirrorBase + "/v3-flatcontainer/", StringComparison.Ordinal),
            Telemetry = telemetry ?? new ProxyFeatureTelemetry(),
            ResourceNotAdvertisedMessage = "Registration resource not advertised by upstream.",
            Ttl = TimeSpan.FromMinutes(30),
            MaxBodyBytes = maxBodyBytes,
            LruTouch = lruTouch,
            CacheContentType = "test",
        };
    }

    private static string ReadBody(DefaultHttpContext context)
    {
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8, leaveOpen: true);
        return reader.ReadToEnd();
    }

    private static CachedProxyPipeline CreatePipeline(
        IPackageContentStore store,
        HttpMessageHandler handler,
        string? publicBaseUrl = null,
        EvictionStrategy evictionStrategy = EvictionStrategy.Oldest,
        bool evictionEnabled = false)
    {
        var options = new MirrorOptions
        {
            PublicBaseUrl = publicBaseUrl,
            Cache = new CacheOptions
            {
                Enabled = true,
                Eviction = new CacheEvictionOptions
                {
                    Enabled = evictionEnabled,
                    Strategy = evictionStrategy,
                },
            },
        };
        IOptions<MirrorOptions> optionsWrapper = Options.Create(options);
        var httpClientFactory = new TestHttpClientFactory(handler);
        var upstreamClient = new UpstreamClient(httpClientFactory, optionsWrapper, NullLogger<UpstreamClient>.Instance, new MirrorMetrics());
        var discovery = new DiscoveryCache(upstreamClient, optionsWrapper, NullLogger<DiscoveryCache>.Instance, new MirrorMetrics(), TimeProvider.System);
        SeedSnapshot(discovery, new Dictionary<string, string> { [ForwardKey] = UpstreamBase });
        var keyedLock = new KeyedAsyncLock(NullLogger<KeyedAsyncLock>.Instance);
        return new CachedProxyPipeline(upstreamClient, discovery, store, keyedLock, optionsWrapper, new MirrorMetrics(), NullLogger.Instance, TimeProvider.System);
    }

    private static CachedProxyPipeline CreatePipelineWithEmptyForwardMap(IPackageContentStore store, string? publicBaseUrl = null)
    {
        var options = new MirrorOptions { PublicBaseUrl = publicBaseUrl, Cache = { Enabled = true } };
        IOptions<MirrorOptions> optionsWrapper = Options.Create(options);
        var handler = new StubUpstreamHandler();
        var httpClientFactory = new TestHttpClientFactory(handler);
        var upstreamClient = new UpstreamClient(httpClientFactory, optionsWrapper, NullLogger<UpstreamClient>.Instance, new MirrorMetrics());
        var discovery = new DiscoveryCache(upstreamClient, optionsWrapper, NullLogger<DiscoveryCache>.Instance, new MirrorMetrics(), TimeProvider.System);
        SeedSnapshot(discovery, []);
        var keyedLock = new KeyedAsyncLock(NullLogger<KeyedAsyncLock>.Instance);
        return new CachedProxyPipeline(upstreamClient, discovery, store, keyedLock, optionsWrapper, new MirrorMetrics(), NullLogger.Instance, TimeProvider.System);
    }

    private static CachedProxyPipeline CreatePipelineWithFailingDiscovery(IPackageContentStore store, string? publicBaseUrl = null)
    {
        var options = new MirrorOptions
        {
            PublicBaseUrl = publicBaseUrl,
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

    private static void SeedSnapshot(DiscoveryCache cache, Dictionary<string, string> forwardMap)
    {
        var snapshot = new DiscoverySnapshot(DateTimeOffset.UtcNow, "{}", forwardMap, []);
        FieldInfo field = typeof(DiscoveryCache).GetField("_current", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("DiscoveryCache._current field not found.");
        field.SetValue(cache, snapshot);
    }

    // ── Inner test doubles ───────────────────────────────────────────────────

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

    private sealed class RewriteStore : IPackageContentStore, ICacheMaintenance, ICacheRevalidation
    {
        public byte[]? CachedBytes { get; init; }
        public long Length { get; init; }
        public string ContentType { get; init; } = "application/json";
        public DateTimeOffset? StoredAt { get; init; }
        public string? ETag { get; init; }
        public bool WithMaintenance { get; init; }
        public bool WithRevalidation { get; init; }
        public bool ReturnsNull { get; init; }

        public bool TouchCalled { get; private set; }
        public List<string> TouchedKeys { get; } = [];
        public bool RevalidationCalled { get; private set; }
        public bool WasWritten { get; private set; }
        public string? WrittenEtag { get; private set; }

        public ValueTask<CachedContent?> TryGetAsync(string key, CancellationToken ct)
        {
            if (ReturnsNull || CachedBytes is null)
            {
                return ValueTask.FromResult<CachedContent?>(null);
            }

            return ValueTask.FromResult<CachedContent?>(new CachedContent
            {
                Stream = new MemoryStream(CachedBytes),
                Length = Length != 0 ? Length : CachedBytes.Length,
                ContentType = ContentType,
                ETag = ETag,
                StoredAtUtc = StoredAt ?? DateTimeOffset.UtcNow,
            });
        }

        public ValueTask<ICacheWriteHandle> BeginWriteAsync(string key, CancellationToken ct)
            => ValueTask.FromResult<ICacheWriteHandle>(new RewriteWriteHandle((bytes, etag) =>
            {
                WasWritten = bytes > 0;
                WrittenEtag = etag;
            }));

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

        public ValueTask RefreshTimestampAsync(string key, string contentType, string? etag, DateTimeOffset fetchedAtUtc, CancellationToken ct)
        {
            RevalidationCalled = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DelayedRewriteStore(byte[] cachedBytes, int hitOnAttempt) : IPackageContentStore, ICacheMaintenance
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
                    ContentType = "application/json",
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

    private sealed class RewriteWriteHandle(Action<int, string?> onCommit) : ICacheWriteHandle
    {
        private readonly MemoryStream _stream = new();
        private string? _etag;

        public Stream Stream => _stream;

        public void SetMetadata(string contentType, long length, string? etag)
        {
            _etag = etag;
        }

        public ValueTask CommitAsync(CancellationToken ct)
        {
            onCommit((int)_stream.Length, _etag);
            return ValueTask.CompletedTask;
        }

        public async ValueTask DisposeAsync() => await _stream.DisposeAsync();
    }
}
