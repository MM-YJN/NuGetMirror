using System.Net;
using System.Reflection;
using System.Text;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NuGetMirror.Configuration;
using NuGetMirror.Diagnostics;
using NuGetMirror.Discovery;
using NuGetMirror.Proxy;
using NuGetMirror.TestKit;
using NuGetMirror.Upstream;

namespace NuGetMirror.UnitTests;

public sealed class ForwarderRewriteTests
{
    private const string RegistrationPrefix = "/v3/registration-semver2/";

    [Fact]
    public async Task RewriteProxy_Returns404_WhenRouteNotFound()
    {
        DefaultHttpContext context = CreateHttpContext();
        Forwarder forwarder = CreateForwarderWithDiscovery(new Dictionary<string, string>
        {
            ["/v3-flatcontainer/"] = "https://api.nuget.org/v3-flatcontainer/",
        });

        await forwarder.RewriteProxyAsync(context, RegistrationPrefix, "newtonsoft.json/index.json");

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
    }

    [Fact]
    public async Task RewriteProxy_Returns502_WhenDiscoveryFails()
    {
        DefaultHttpContext context = CreateHttpContext();
        Forwarder forwarder = CreateForwarderWithFailingDiscovery();

        await forwarder.RewriteProxyAsync(context, RegistrationPrefix, "newtonsoft.json/index.json");

        Assert.Equal(StatusCodes.Status502BadGateway, context.Response.StatusCode);
    }

    [Fact]
    public async Task RewriteProxy_Returns502_WhenUpstreamFails()
    {
        DefaultHttpContext context = CreateHttpContext();
        Forwarder forwarder = CreateForwarderWithFailingUpstream(RegistrationPrefix);

        await forwarder.RewriteProxyAsync(context, RegistrationPrefix, "newtonsoft.json/index.json");

        Assert.Equal(StatusCodes.Status502BadGateway, context.Response.StatusCode);
    }

    [Fact]
    public async Task RewriteProxy_Returns200_WithRewrittenContent()
    {
        DefaultHttpContext context = CreateHttpContext();
        string upstreamBase = "https://api.nuget.org/v3/registration5-gz-semver2/";
        string forwarding = RegistrationPrefix;
        string upstreamJson = """
        {
          "items": [
            {
              "packageContent": "https://api.nuget.org/v3-flatcontainer/newtonsoft.json/13.0.3/newtonsoft.json.13.0.3.nupkg"
            }
          ]
        }
        """;
        Forwarder forwarder = CreateForwarderForRewrite(forwarding, upstreamBase, upstreamJson);

        await forwarder.RewriteProxyAsync(context, forwarding, "newtonsoft.json/index.json");

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("application/json", context.Response.ContentType);

        string body = ReadResponseBody(context);
        Assert.DoesNotContain("api.nuget.org", body, StringComparison.Ordinal);
        Assert.Contains("http://localhost/v3-flatcontainer/", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RewriteProxy_SetsContentLength_MatchingRewrittenBodyByteCount()
    {
        // WriteBodyUtf8Async sets Content-Length from Encoding.UTF8.GetByteCount before
        // writing the body.  This test verifies that the header matches the actual bytes
        // flushed to the response — critical for HTTP/1.1 framing and HTTP/2 DATA frames.
        DefaultHttpContext context = CreateHttpContext();
        string upstreamBase = "https://api.nuget.org/v3/registration5-gz-semver2/";
        // Only flat-container URLs in the body so the rewrite pair replaces all of them.
        string upstreamJson = """{"items":[{"packageContent":"https://api.nuget.org/v3-flatcontainer/newtonsoft.json/13.0.3/newtonsoft.json.13.0.3.nupkg"}]}""";
        Forwarder forwarder = CreateForwarderForRewrite(RegistrationPrefix, upstreamBase, upstreamJson);

        await forwarder.RewriteProxyAsync(context, RegistrationPrefix, "newtonsoft.json/index.json");

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);

        string responseBody = ReadResponseBody(context);
        int expectedByteCount = Encoding.UTF8.GetByteCount(responseBody);

        Assert.Equal(expectedByteCount, context.Response.ContentLength);
        // Flat-container URLs should be rewritten; no upstream hostname should remain.
        Assert.DoesNotContain("api.nuget.org", responseBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RewriteProxy_ContentLength_ReflectsPostRewriteSize()
    {
        // RecordUpstreamBytes receives the raw upstream byte count (before URL rewriting),
        // while Content-Length carries the post-rewrite count.  When upstream URLs are
        // longer than mirror URLs the rewritten body is shorter.
        // Here "https://api.nuget.org/v3-flatcontainer/" (41 chars) is replaced with
        // "http://localhost/v3-flatcontainer/" (32 chars), so the rewritten body is smaller.
        DefaultHttpContext context = CreateHttpContext();
        string upstreamBase = "https://api.nuget.org/v3/registration5-gz-semver2/";
        const string UpstreamUrl = "https://api.nuget.org/v3-flatcontainer/newtonsoft.json/13.0.3/newtonsoft.json.13.0.3.nupkg";
        string upstreamJson = $$"""{"packageContent":"{{UpstreamUrl}}"}""";
        Forwarder forwarder = CreateForwarderForRewrite(RegistrationPrefix, upstreamBase, upstreamJson);

        await forwarder.RewriteProxyAsync(context, RegistrationPrefix, "newtonsoft.json/index.json");

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);

        string responseBody = ReadResponseBody(context);
        int upstreamBodyBytes = Encoding.UTF8.GetByteCount(upstreamJson);
        int rewrittenBodyBytes = Encoding.UTF8.GetByteCount(responseBody);

        // The rewritten size must match the Content-Length header.
        Assert.Equal(rewrittenBodyBytes, context.Response.ContentLength);

        // The upstream URL is longer than the mirror URL, so the rewritten body is smaller.
        // This confirms the rewrite occurred and Content-Length tracks the post-rewrite count.
        Assert.True(
            rewrittenBodyBytes < upstreamBodyBytes,
            $"Expected rewritten body ({rewrittenBodyBytes} B) to be smaller than upstream body ({upstreamBodyBytes} B).");
    }

    [Fact]
    public async Task RewriteProxy_PassesThrough_UpstreamNon200Status()
    {
        DefaultHttpContext context = CreateHttpContext();
        string forwarding = RegistrationPrefix;
        string upstreamBase = "https://api.nuget.org/v3/registration5-gz-semver2/";

        IOptions<MirrorOptions> optionsWrapper = Microsoft.Extensions.Options.Options.Create(new MirrorOptions
        {
            Cache = new CacheOptions { Enabled = false },
            PublicBaseUrl = "http://localhost",
        });

        var upstreamHandler = new StubUpstreamHandler();
        upstreamHandler.MapFactory(
            "https://api.nuget.org/v3/index.json",
            () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(TestFixtures.NuGetOrgServiceIndex, Encoding.UTF8, "application/json"),
            });
        upstreamHandler.MapFactory(
            upstreamBase + "newtonsoft.json/index.json",
            () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("""{"error":"upstream error"}""", Encoding.UTF8, "application/json"),
            });

        var httpClient = new TestHttpClientFactory(upstreamHandler);
        var upstreamClient = new UpstreamClient(httpClient, optionsWrapper, NullLogger<UpstreamClient>.Instance, new MirrorMetrics());
        var cache = new DiscoveryCache(upstreamClient, optionsWrapper, NullLogger<DiscoveryCache>.Instance, new MirrorMetrics(), TimeProvider.System);

        var forwarder = new Forwarder(upstreamClient, cache, optionsWrapper, NullLogger<Forwarder>.Instance, new MirrorMetrics());

        await forwarder.RewriteProxyAsync(context, forwarding, "newtonsoft.json/index.json");

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.Equal("application/json", context.Response.ContentType);

        string body = ReadResponseBody(context);
        Assert.Contains("upstream error", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RewriteProxy_Returns502_WhenBodyExceedsSizeCap()
    {
        DefaultHttpContext context = CreateHttpContext();
        string routePrefix = RegistrationPrefix;
        string upstreamBase = "https://api.nuget.org/v3/registration5-gz-semver2/";
        string upstreamJson = new('x', 100);

        IOptions<MirrorOptions> optionsWrapper = Microsoft.Extensions.Options.Options.Create(new MirrorOptions
        {
            Cache = new CacheOptions { Enabled = false },
            PublicBaseUrl = "http://localhost",
            Upstream = { MaxRewriteBodyBytes = 10 },
        });

        var handler = new StubUpstreamHandler();
        handler.MapFactory(
            upstreamBase + "newtonsoft.json/index.json",
            () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(upstreamJson, Encoding.UTF8, "application/json"),
            });

        var httpClient = new TestHttpClientFactory(handler);
        var upstreamClient = new UpstreamClient(httpClient, optionsWrapper, NullLogger<UpstreamClient>.Instance, new MirrorMetrics());

        var snapshot = new DiscoverySnapshot(
            DateTimeOffset.UtcNow,
            upstreamJson,
            new Dictionary<string, string> { [routePrefix] = upstreamBase },
            [new RewritePair("https://api.nuget.org/v3-flatcontainer/", "/v3-flatcontainer/")]);
        var cache = new DiscoveryCache(upstreamClient, optionsWrapper, NullLogger<DiscoveryCache>.Instance, new MirrorMetrics(), TimeProvider.System);
        SeedSnapshot(cache, snapshot);

        var forwarder = new Forwarder(upstreamClient, cache, optionsWrapper, NullLogger<Forwarder>.Instance, new MirrorMetrics());

        await forwarder.RewriteProxyAsync(context, routePrefix, "newtonsoft.json/index.json");

        Assert.Equal(StatusCodes.Status502BadGateway, context.Response.StatusCode);
    }

    private static DefaultHttpContext CreateHttpContext()
    {
        var context = new DefaultHttpContext();
        var body = new MemoryStream();
        context.Response.Body = body;

        context.Request.Method = HttpMethods.Get;
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("localhost:5049");

        return context;
    }

    private static Forwarder CreateForwarderWithDiscovery(Dictionary<string, string> forwardMap)
    {
        IOptions<MirrorOptions> optionsWrapper = Microsoft.Extensions.Options.Options.Create(new MirrorOptions
        {
            Cache = new CacheOptions { Enabled = true },
        });

        var snapshot = new DiscoverySnapshot(DateTimeOffset.UtcNow, "{}", forwardMap, []);

        var httpClient = new TestHttpClientFactory(new StubUpstreamHandler());
        var upstreamClient = new UpstreamClient(httpClient, optionsWrapper, NullLogger<UpstreamClient>.Instance, new MirrorMetrics());
        var cache = new DiscoveryCache(upstreamClient, optionsWrapper, NullLogger<DiscoveryCache>.Instance, new MirrorMetrics(), TimeProvider.System);

        SeedSnapshot(cache, snapshot);

        return new Forwarder(upstreamClient, cache, optionsWrapper, NullLogger<Forwarder>.Instance, new MirrorMetrics());
    }

    private static Forwarder CreateForwarderWithFailingDiscovery()
    {
        IOptions<MirrorOptions> optionsWrapper = Microsoft.Extensions.Options.Options.Create(new MirrorOptions
        {
            Upstream = { DiscoveryCacheTtl = TimeSpan.Zero },
            Cache = new CacheOptions { Enabled = true },
        });

        var handler = new FailingHttpMessageHandler();
        var httpClient = new TestHttpClientFactory(handler);
        var upstreamClient = new UpstreamClient(httpClient, optionsWrapper, NullLogger<UpstreamClient>.Instance, new MirrorMetrics());
        var cache = new DiscoveryCache(upstreamClient, optionsWrapper, NullLogger<DiscoveryCache>.Instance, new MirrorMetrics(), TimeProvider.System);

        return new Forwarder(upstreamClient, cache, optionsWrapper, NullLogger<Forwarder>.Instance, new MirrorMetrics());
    }

    private static Forwarder CreateForwarderWithFailingUpstream(string routePrefix)
    {
        IOptions<MirrorOptions> optionsWrapper = Microsoft.Extensions.Options.Options.Create(new MirrorOptions
        {
            Cache = new CacheOptions { Enabled = true },
        });

        var httpClient = new TestHttpClientFactory(new FailingHttpMessageHandler());
        var upstreamClient = new UpstreamClient(httpClient, optionsWrapper, NullLogger<UpstreamClient>.Instance, new MirrorMetrics());

        var snapshot = new DiscoverySnapshot(
            DateTimeOffset.UtcNow,
            "{}",
            new Dictionary<string, string> { [routePrefix] = "https://api.nuget.org/v3/registration5-gz-semver2/" },
            []);
        var cache = new DiscoveryCache(upstreamClient, optionsWrapper, NullLogger<DiscoveryCache>.Instance, new MirrorMetrics(), TimeProvider.System);
        SeedSnapshot(cache, snapshot);

        return new Forwarder(upstreamClient, cache, optionsWrapper, NullLogger<Forwarder>.Instance, new MirrorMetrics());
    }

    private static Forwarder CreateForwarderForRewrite(string routePrefix, string upstreamBase, string upstreamJson)
    {
        IOptions<MirrorOptions> optionsWrapper = Microsoft.Extensions.Options.Options.Create(new MirrorOptions
        {
            Cache = new CacheOptions { Enabled = false },
            PublicBaseUrl = "http://localhost",
        });

        var handler = new StubUpstreamHandler();
        handler.MapFactory(
            upstreamBase + "newtonsoft.json/index.json",
            () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(upstreamJson, Encoding.UTF8, "application/json"),
            });

        var httpClient = new TestHttpClientFactory(handler);
        var upstreamClient = new UpstreamClient(httpClient, optionsWrapper, NullLogger<UpstreamClient>.Instance, new MirrorMetrics());

        var snapshot = new DiscoverySnapshot(
            DateTimeOffset.UtcNow,
            upstreamJson,
            new Dictionary<string, string> { [routePrefix] = upstreamBase },
            [new RewritePair("https://api.nuget.org/v3-flatcontainer/", "/v3-flatcontainer/")]);
        var cache = new DiscoveryCache(upstreamClient, optionsWrapper, NullLogger<DiscoveryCache>.Instance, new MirrorMetrics(), TimeProvider.System);
        SeedSnapshot(cache, snapshot);

        return new Forwarder(upstreamClient, cache, optionsWrapper, NullLogger<Forwarder>.Instance, new MirrorMetrics());
    }

    private static void SeedSnapshot(DiscoveryCache cache, DiscoverySnapshot snapshot)
    {
        FieldInfo field = typeof(DiscoveryCache).GetField(
            "_current",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("DiscoveryCache._current field not found.");

        field.SetValue(cache, snapshot);
    }

    private static string ReadResponseBody(DefaultHttpContext context)
    {
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8, leaveOpen: true);
        return reader.ReadToEnd();
    }

    private sealed class FailingHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("Simulated upstream failure");
    }
}
