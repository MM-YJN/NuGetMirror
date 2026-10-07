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

public sealed class ForwarderStreamProxyLiveTests
{
    private const string RoutePrefix = "/v3-flatcontainer/";
    private const string UpstreamBase = "http://upstream.example/v3-flatcontainer/";
    private const string RemainingPath = "test.pkg/1.0.0/test.pkg.1.0.0.nupkg";
    private static readonly byte[] s_bodyBytes = "fake-nupkg-content"u8.ToArray();

    private static string UpstreamUrl => UpstreamBase + RemainingPath;

    // ── Discovery failure ────────────────────────────────────────────────────

    [Fact]
    public async Task DiscoveryFails_Returns502Problem()
    {
        const string MeterName = "StreamProxyLive.DiscoveryFails";
        using var metrics = new MirrorMetrics(meterName: MeterName);
        var options = new MirrorOptions
        {
            Cache = { Enabled = false },
            Upstream = { DiscoveryCacheTtl = TimeSpan.Zero },
        };
        var handler = new FailingHttpMessageHandler();
        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        Forwarder forwarder = CreateForwarder(options, handler, forwardMap: null, metrics);

        using var capture = new MetricsCapture(MeterName);
        await forwarder.StreamProxyAsync(context, RoutePrefix, RemainingPath);

        Assert.Equal(StatusCodes.Status502BadGateway, context.Response.StatusCode);
        Assert.Equal("application/problem+json", context.Response.ContentType);
        string body = ReadStringBody(context);
        Assert.Contains("Simulated upstream failure", body, StringComparison.Ordinal);

        Measurement? errorMetric = capture.Find("nugetmirror.proxy.errors", "kind", "discovery");
        Assert.NotNull(errorMetric);
        Assert.Equal("502", errorMetric!.GetTag("status"));
    }

    // ── Route not found ──────────────────────────────────────────────────────

    [Fact]
    public async Task RouteNotFound_Returns404()
    {
        const string MeterName = "StreamProxyLive.RouteNotFound";
        using var metrics = new MirrorMetrics(meterName: MeterName);
        var options = new MirrorOptions { Cache = { Enabled = false } };
        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        Forwarder forwarder = CreateForwarder(options, new StubUpstreamHandler(), [], metrics);

        using var capture = new MetricsCapture(MeterName);
        await forwarder.StreamProxyAsync(context, RoutePrefix, RemainingPath);

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);

        Measurement? errorMetric = capture.Find("nugetmirror.proxy.errors", "kind", "not_found");
        Assert.NotNull(errorMetric);
        Assert.Equal("404", errorMetric!.GetTag("status"));
    }

    // ── Negative cache hit ───────────────────────────────────────────────────

    [Fact]
    public async Task NegativeCacheHit_Returns404_AndDoesNotCallUpstream()
    {
        const string MeterName = "StreamProxyLive.NegativeCacheHit";
        using var metrics = new MirrorMetrics(meterName: MeterName);
        var negCache = new NegativeCache(10, TimeProvider.System);
        negCache.Store(RoutePrefix + RemainingPath, TimeSpan.FromMinutes(1));

        var options = new MirrorOptions
        {
            Cache =
            {
                Enabled = true,
                NegativeCache = { Enabled = true },
            },
        };
        var handler = new StubUpstreamHandler();
        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        Forwarder forwarder = CreateForwarder(options, handler, DefaultForwardMap(), metrics, negativeCache: negCache);

        using var capture = new MetricsCapture(MeterName);
        await forwarder.StreamProxyAsync(context, RoutePrefix, RemainingPath);

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Equal(0, handler.GetCount(UpstreamUrl));

        Measurement? negHitMetric = capture.Find("nugetmirror.cache.requests", "result", "neg_hit");
        Assert.NotNull(negHitMetric);
        Assert.Equal("package", negHitMetric!.GetTag("content_type"));
    }

    // ── Negative cache store on 404 ──────────────────────────────────────────

    [Fact]
    public async Task Upstream404_StoresInNegativeCache()
    {
        const string MeterName = "StreamProxyLive.Upstream404";
        using var metrics = new MirrorMetrics(meterName: MeterName);
        var negCache = new NegativeCache(10, TimeProvider.System);
        var options = new MirrorOptions
        {
            Cache =
            {
                Enabled = true,
                NegativeCache = { Enabled = true },
            },
        };
        var handler = new StubUpstreamHandler();
        handler.MapFactory(UpstreamUrl, () =>
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new ByteArrayContent([]),
            };
        });
        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        Forwarder forwarder = CreateForwarder(options, handler, DefaultForwardMap(), metrics, negativeCache: negCache);

        using var capture = new MetricsCapture(MeterName);
        await forwarder.StreamProxyAsync(context, RoutePrefix, RemainingPath);

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.True(negCache.TryGet(RoutePrefix + RemainingPath));

        Measurement? negStoreMetric = capture.Find("nugetmirror.cache.requests", "result", "neg_store");
        Assert.NotNull(negStoreMetric);
        Assert.Equal("package", negStoreMetric!.GetTag("content_type"));
    }

    // ── Negative cache store on 410 Gone ─────────────────────────────────────

    [Fact]
    public async Task Upstream410Gone_StoresInNegativeCache()
    {
        const string MeterName = "StreamProxyLive.Upstream410Gone";
        using var metrics = new MirrorMetrics(meterName: MeterName);
        var negCache = new NegativeCache(10, TimeProvider.System);
        var options = new MirrorOptions
        {
            Cache =
            {
                Enabled = true,
                NegativeCache = { Enabled = true },
            },
        };
        var handler = new StubUpstreamHandler();
        handler.MapFactory(UpstreamUrl, () =>
        {
            return new HttpResponseMessage(HttpStatusCode.Gone)
            {
                Content = new ByteArrayContent([]),
            };
        });
        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        Forwarder forwarder = CreateForwarder(options, handler, DefaultForwardMap(), metrics, negativeCache: negCache);

        using var capture = new MetricsCapture(MeterName);
        await forwarder.StreamProxyAsync(context, RoutePrefix, RemainingPath);

        Assert.Equal(StatusCodes.Status410Gone, context.Response.StatusCode);
        Assert.True(negCache.TryGet(RoutePrefix + RemainingPath));

        Measurement? negStoreMetric = capture.Find("nugetmirror.cache.requests", "result", "neg_store");
        Assert.NotNull(negStoreMetric);
        Assert.Equal("package", negStoreMetric!.GetTag("content_type"));
    }

    // ── HEAD request ─────────────────────────────────────────────────────────

    [Fact]
    public async Task HeadRequest_CopiesHeadersWithoutBody()
    {
        using var metrics = new MirrorMetrics();
        var options = new MirrorOptions { Cache = { Enabled = false } };
        var handler = new StubUpstreamHandler();
        handler.MapFactory(UpstreamUrl, () =>
        {
            var content = new ByteArrayContent(s_bodyBytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Headers.ContentLength = s_bodyBytes.Length;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        DefaultHttpContext context = TestContextFactory.MakeHeadContext();
        Forwarder forwarder = CreateForwarder(options, handler, DefaultForwardMap(), metrics);

        await forwarder.StreamProxyAsync(context, RoutePrefix, RemainingPath);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("application/octet-stream", context.Response.ContentType);
        Assert.Equal(s_bodyBytes.Length, context.Response.ContentLength);
        Assert.Empty(ReadBody(context));
    }

    // ── No Content-Length → no byte metrics ──────────────────────────────────

    [Fact]
    public async Task NoContentLength_DoesNotRecordByteMetrics()
    {
        const string MeterName = "StreamProxyLive.NoContentLength";
        using var metrics = new MirrorMetrics(meterName: MeterName);
        var options = new MirrorOptions { Cache = { Enabled = false } };
        var handler = new StubUpstreamHandler();
        handler.MapFactory(UpstreamUrl, () =>
        {
            var stream = new NonSeekableStream(new MemoryStream(s_bodyBytes));
            var content = new StreamContent(stream);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        Forwarder forwarder = CreateForwarder(options, handler, DefaultForwardMap(), metrics);

        using var capture = new MetricsCapture(MeterName);
        await forwarder.StreamProxyAsync(context, RoutePrefix, RemainingPath);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(s_bodyBytes, ReadBody(context));
        Assert.Null(capture.Find("nugetmirror.upstream.bytes"));
        Assert.Null(capture.Find("nugetmirror.served.bytes"));
    }

    // ── Non-200 status passthrough ───────────────────────────────────────────

    [Fact]
    public async Task Non200Status_PassesThroughStatusAndBody()
    {
        using var metrics = new MirrorMetrics();
        byte[] errorBytes = "internal-error"u8.ToArray();
        var options = new MirrorOptions { Cache = { Enabled = false } };
        var handler = new StubUpstreamHandler();
        handler.MapFactory(UpstreamUrl, () =>
        {
            var content = new ByteArrayContent(errorBytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
            content.Headers.ContentLength = errorBytes.Length;
            return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = content };
        });
        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        Forwarder forwarder = CreateForwarder(options, handler, DefaultForwardMap(), metrics);

        await forwarder.StreamProxyAsync(context, RoutePrefix, RemainingPath);

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.Equal("text/plain", context.Response.ContentType);
        Assert.Equal(errorBytes, ReadBody(context));
    }

    // ── Upstream headers copied ──────────────────────────────────────────────

    [Fact]
    public async Task UpstreamHeaders_CopiedToResponse()
    {
        using var metrics = new MirrorMetrics();
        var options = new MirrorOptions { Cache = { Enabled = false } };
        var handler = new StubUpstreamHandler();
        handler.MapFactory(UpstreamUrl, () =>
        {
            var content = new ByteArrayContent(s_bodyBytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Headers.ContentLength = s_bodyBytes.Length;
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            response.Headers.ETag = new EntityTagHeaderValue("\"abc123\"");
            response.Headers.TryAddWithoutValidation("Accept-Ranges", "bytes");
            response.Content.Headers.LastModified = new DateTimeOffset(2025, 1, 15, 10, 30, 0, TimeSpan.Zero);
            return response;
        });
        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        Forwarder forwarder = CreateForwarder(options, handler, DefaultForwardMap(), metrics);

        await forwarder.StreamProxyAsync(context, RoutePrefix, RemainingPath);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("\"abc123\"", context.Response.Headers.ETag);
        Assert.Equal("bytes", context.Response.Headers.AcceptRanges);
        string lastModifiedHeader = context.Response.Headers.LastModified.ToString();
        Assert.Contains("15 Jan 2025", lastModifiedHeader, StringComparison.Ordinal);
        Assert.Equal(s_bodyBytes, ReadBody(context));
    }

    // ── Client cancellation ──────────────────────────────────────────────────

    [Fact]
    public async Task ClientCancellation_RecordsCancellation()
    {
        const string MeterName = "StreamProxyLive.ClientCancel";
        using var metrics = new MirrorMetrics(meterName: MeterName);
        var cts = new CancellationTokenSource();
        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        context.RequestAborted = cts.Token;
        await cts.CancelAsync();

        var options = new MirrorOptions { Cache = { Enabled = false } };
        var handler = new TokenObservingHandler();
        Forwarder forwarder = CreateForwarder(options, handler, DefaultForwardMap(), metrics);

        using var capture = new MetricsCapture(MeterName);
        await forwarder.StreamProxyAsync(context, RoutePrefix, RemainingPath);

        Measurement? cancelMeasurement = capture.Find("nugetmirror.client.cancellations", "stage", "live");
        Assert.NotNull(cancelMeasurement);
        Assert.Equal(1, cancelMeasurement!.Value);
    }

    // ── HttpRequestException → 502 ───────────────────────────────────────────

    [Fact]
    public async Task HttpRequestException_Returns502Problem()
    {
        const string MeterName = "StreamProxyLive.HttpRequestException";
        using var metrics = new MirrorMetrics(meterName: MeterName);
        var options = new MirrorOptions { Cache = { Enabled = false } };
        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        Forwarder forwarder = CreateForwarder(options, new FailingHttpMessageHandler(), DefaultForwardMap(), metrics);

        using var capture = new MetricsCapture(MeterName);
        await forwarder.StreamProxyAsync(context, RoutePrefix, RemainingPath);

        Assert.Equal(StatusCodes.Status502BadGateway, context.Response.StatusCode);
        Assert.Equal("application/problem+json", context.Response.ContentType);
        string body = ReadStringBody(context);
        Assert.Contains("Simulated upstream failure", body, StringComparison.Ordinal);

        Measurement? errorMetric = capture.Find("nugetmirror.proxy.errors", "kind", "upstream");
        Assert.NotNull(errorMetric);
        Assert.Equal("502", errorMetric!.GetTag("status"));
    }

    // ── ExecutionRejectedException → 502 ─────────────────────────────────────

    [Fact]
    public async Task ExecutionRejectedException_Returns502Problem()
    {
        const string MeterName = "StreamProxyLive.ExecutionRejected";
        using var metrics = new MirrorMetrics(meterName: MeterName);
        var options = new MirrorOptions { Cache = { Enabled = false } };
        var handler = new RejectingHandler();
        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        Forwarder forwarder = CreateForwarder(options, handler, DefaultForwardMap(), metrics);

        using var capture = new MetricsCapture(MeterName);
        await forwarder.StreamProxyAsync(context, RoutePrefix, RemainingPath);

        Assert.Equal(StatusCodes.Status502BadGateway, context.Response.StatusCode);
        Assert.Equal("application/problem+json", context.Response.ContentType);
        string body = ReadStringBody(context);
        Assert.Contains("Simulated Polly rejection", body, StringComparison.Ordinal);

        Measurement? errorMetric = capture.Find("nugetmirror.proxy.errors", "kind", "upstream");
        Assert.NotNull(errorMetric);
        Assert.Equal("502", errorMetric!.GetTag("status"));
    }

    // ── Success records byte metrics ─────────────────────────────────────────

    [Fact]
    public async Task Success_RecordsByteMetrics()
    {
        const string MeterName = "StreamProxyLive.SuccessMetrics";
        using var metrics = new MirrorMetrics(meterName: MeterName);
        var options = new MirrorOptions { Cache = { Enabled = false } };
        var handler = new StubUpstreamHandler();
        handler.MapFactory(UpstreamUrl, () =>
        {
            var content = new ByteArrayContent(s_bodyBytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Headers.ContentLength = s_bodyBytes.Length;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        Forwarder forwarder = CreateForwarder(options, handler, DefaultForwardMap(), metrics);

        using var capture = new MetricsCapture(MeterName);
        await forwarder.StreamProxyAsync(context, RoutePrefix, RemainingPath);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(s_bodyBytes, ReadBody(context));

        Measurement? upstreamBytes = capture.Find("nugetmirror.upstream.bytes");
        Assert.NotNull(upstreamBytes);
        Assert.Equal(s_bodyBytes.Length, upstreamBytes!.Value);

        Measurement? servedBytes = capture.Find("nugetmirror.served.bytes", "source", "upstream");
        Assert.NotNull(servedBytes);
        Assert.Equal(s_bodyBytes.Length, servedBytes!.Value);
    }

    // ── TaskCanceledException inner + client abort ───────────────────────────

    [Fact]
    public async Task TaskCanceledInnerHttpRequestException_OnClientAbort_RecordsCancellation()
    {
        const string MeterName = "StreamProxyLive.TaskCanceledInner";
        using var metrics = new MirrorMetrics(meterName: MeterName);
        var cts = new CancellationTokenSource();
        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        context.RequestAborted = cts.Token;
        await cts.CancelAsync();

        var options = new MirrorOptions { Cache = { Enabled = false } };
        var handler = new TaskCanceledInnerHandler();
        Forwarder forwarder = CreateForwarder(options, handler, DefaultForwardMap(), metrics);

        using var capture = new MetricsCapture(MeterName);
        await forwarder.StreamProxyAsync(context, RoutePrefix, RemainingPath);

        Measurement? cancelMeasurement = capture.Find("nugetmirror.client.cancellations", "stage", "live");
        Assert.NotNull(cancelMeasurement);
        Assert.Equal(1, cancelMeasurement!.Value);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

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

    private static Dictionary<string, string> DefaultForwardMap()
    {
        return new Dictionary<string, string> { [RoutePrefix] = UpstreamBase };
    }

    private static Forwarder CreateForwarder(
        MirrorOptions options,
        HttpMessageHandler handler,
        Dictionary<string, string>? forwardMap,
        MirrorMetrics metrics,
        NegativeCache? negativeCache = null)
    {
        IOptions<MirrorOptions> optionsWrapper = Options.Create(options);
        var httpClientFactory = new TestHttpClientFactory(handler);
        var upstreamClient = new UpstreamClient(httpClientFactory, optionsWrapper, NullLogger<UpstreamClient>.Instance, metrics);
        var discovery = new DiscoveryCache(upstreamClient, optionsWrapper, NullLogger<DiscoveryCache>.Instance, metrics, TimeProvider.System);

        if (forwardMap is not null)
        {
            var snapshot = new DiscoverySnapshot(DateTimeOffset.UtcNow, "{}", forwardMap, []);
            FieldInfo field = typeof(DiscoveryCache).GetField(
                "_current",
                BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new InvalidOperationException("DiscoveryCache._current field not found.");
            field.SetValue(discovery, snapshot);
        }

        return new Forwarder(upstreamClient, discovery, optionsWrapper, NullLogger<Forwarder>.Instance, metrics, store: null, keyedLock: null, negativeCache: negativeCache);
    }

    // ── Inner test doubles ───────────────────────────────────────────────────

    private sealed class FailingHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("Simulated upstream failure");
    }

    private sealed class TokenObservingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class RejectingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new TestExecutionRejectedException("Simulated Polly rejection");
    }

    private sealed class TaskCanceledInnerHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("timeout", new TaskCanceledException());
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1032:Implement standard exception constructors", Justification = "Test-only exception; only message constructor is needed.")]
    private sealed class TestExecutionRejectedException(string message) : ExecutionRejectedException(message);
}
