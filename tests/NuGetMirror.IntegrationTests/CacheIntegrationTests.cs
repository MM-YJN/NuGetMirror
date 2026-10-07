using System.Net;
using System.Net.Http.Headers;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using NuGetMirror.Configuration;
using NuGetMirror.Storage;
using NuGetMirror.TestKit;

namespace NuGetMirror.IntegrationTests;

public sealed class CacheIntegrationTests : IAsyncDisposable
{
    private readonly NuGetMirrorWebApplicationFactory _factory;
    private readonly StubUpstreamHandler _upstreamHandler;
    private readonly string _cacheDir;
    private readonly ITestOutputHelper _testOutputHelper;

    public CacheIntegrationTests(ITestOutputHelper testOutputHelper)
    {
        _testOutputHelper = testOutputHelper;
        _cacheDir = Path.Join(Path.GetTempPath(), "nuget-mirror-cache-int-" + Guid.NewGuid().ToString("N"));
        _upstreamHandler = new StubUpstreamHandler();

        _upstreamHandler
            .MapJson("https://api.nuget.org/v3/index.json", TestFixtures.NuGetOrgServiceIndex)
            .MapJson(
                "https://api.nuget.org/v3-flatcontainer/newtonsoft.json/index.json",
                """{"versions":["12.0.3","13.0.1","13.0.3"]}""")
            .MapBytes(
                "https://api.nuget.org/v3-flatcontainer/newtonsoft.json/13.0.3/newtonsoft.json.13.0.3.nupkg",
                "test-nupkg-content-bytes"u8.ToArray(),
                "application/octet-stream");

        _factory = new NuGetMirrorWebApplicationFactory(testOutputHelper, builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.AddHttpClient("upstream-buffered")
                    .ConfigurePrimaryHttpMessageHandler(() => _upstreamHandler);
                services.AddHttpClient("upstream-stream")
                    .ConfigurePrimaryHttpMessageHandler(() => _upstreamHandler);

                var store = new FileSystemPackageContentStore(
                    Microsoft.Extensions.Options.Options.Create(new MirrorOptions
                    {
                        Cache = new CacheOptions { FileSystem = new FileSystemOptions { Directory = _cacheDir }, Enabled = true },
                    }),
                    NullLogger<FileSystemPackageContentStore>.Instance,
                    TimeProvider.System);

                services.AddSingleton<IPackageContentStore>(store);
            });
        });
    }

    [Fact]
    public async Task GetNupkg_CachesOnFirstRequestAndServesFromCacheOnSecond()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient client = _factory.CreateClient();
        string url = "/v3-flatcontainer/newtonsoft.json/13.0.3/newtonsoft.json.13.0.3.nupkg";

        HttpResponseMessage response1 = await client.GetAsync(url, ct);
        response1.EnsureSuccessStatusCode();
        string body1 = await response1.Content.ReadAsStringAsync(ct);
        Assert.Contains("test-nupkg-content-bytes", body1, StringComparison.Ordinal);
        Assert.Equal(1, _upstreamHandler.GetCount("https://api.nuget.org/v3-flatcontainer/newtonsoft.json/13.0.3/newtonsoft.json.13.0.3.nupkg"));

        HttpResponseMessage response2 = await client.GetAsync(url, ct);
        response2.EnsureSuccessStatusCode();
        string body2 = await response2.Content.ReadAsStringAsync(ct);
        Assert.Contains("test-nupkg-content-bytes", body2, StringComparison.Ordinal);
        Assert.Equal(1, _upstreamHandler.GetCount("https://api.nuget.org/v3-flatcontainer/newtonsoft.json/13.0.3/newtonsoft.json.13.0.3.nupkg"));
    }

    [Fact]
    public async Task GetIndexJson_AlwaysContactsUpstream()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient client = _factory.CreateClient();
        string url = "/v3-flatcontainer/newtonsoft.json/index.json";

        HttpResponseMessage response1 = await client.GetAsync(url, ct);
        response1.EnsureSuccessStatusCode();
        string body1 = await response1.Content.ReadAsStringAsync(ct);
        Assert.Contains("13.0.3", body1, StringComparison.Ordinal);
        int count1 = _upstreamHandler.GetCount("https://api.nuget.org/v3-flatcontainer/newtonsoft.json/index.json");
        Assert.Equal(1, count1);

        HttpResponseMessage response2 = await client.GetAsync(url, ct);
        response2.EnsureSuccessStatusCode();
        string body2 = await response2.Content.ReadAsStringAsync(ct);
        Assert.Contains("13.0.3", body2, StringComparison.Ordinal);
        int count2 = _upstreamHandler.GetCount("https://api.nuget.org/v3-flatcontainer/newtonsoft.json/index.json");
        Assert.Equal(2, count2);
    }

    [Fact]
    public async Task GetNupkg_Head_ReturnsHeaders()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient client = _factory.CreateClient();
        string url = "/v3-flatcontainer/newtonsoft.json/13.0.3/newtonsoft.json.13.0.3.nupkg";

        var request = new HttpRequestMessage(HttpMethod.Head, url);
        HttpResponseMessage response = await client.SendAsync(request, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.ToString());
    }

    [Fact]
    public async Task GetNupkg_Head_Cached_ReturnsHeaders()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient client = _factory.CreateClient();
        string url = "/v3-flatcontainer/newtonsoft.json/13.0.3/newtonsoft.json.13.0.3.nupkg";

        await client.GetAsync(url, ct);

        var request = new HttpRequestMessage(HttpMethod.Head, url);
        HttpResponseMessage response = await client.SendAsync(request, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.ToString());
    }

    [Fact]
    public async Task GetNupkg_Upstream404_Returns404AndDoesNotCache()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient client = _factory.CreateClient();

        string pkgUrl = "https://api.nuget.org/v3-flatcontainer/missing.pkg/1.0.0/missing.pkg.1.0.0.nupkg";
        _upstreamHandler.MapFactory(pkgUrl, () => new HttpResponseMessage(HttpStatusCode.NotFound));

        string url = "/v3-flatcontainer/missing.pkg/1.0.0/missing.pkg.1.0.0.nupkg";
        HttpResponseMessage response1 = await client.GetAsync(url, ct);
        Assert.Equal(HttpStatusCode.NotFound, response1.StatusCode);
        Assert.Equal(1, _upstreamHandler.GetCount(pkgUrl));

        // Second request is served from the negative cache; upstream is not contacted.
        HttpResponseMessage response2 = await client.GetAsync(url, ct);
        Assert.Equal(HttpStatusCode.NotFound, response2.StatusCode);
        Assert.Equal(1, _upstreamHandler.GetCount(pkgUrl));
    }

    [Fact]
    public async Task GetNupkg_Upstream500_Returns500AndDoesNotCache()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient client = _factory.CreateClient();

        string pkgUrl = "https://api.nuget.org/v3-flatcontainer/server.err/1.0.0/server.err.1.0.0.nupkg";
        _upstreamHandler.MapFactory(pkgUrl, () => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        string url = "/v3-flatcontainer/server.err/1.0.0/server.err.1.0.0.nupkg";
        HttpResponseMessage response1 = await client.GetAsync(url, ct);
        Assert.Equal(HttpStatusCode.InternalServerError, response1.StatusCode);
        Assert.Equal(1, _upstreamHandler.GetCount(pkgUrl));

        HttpResponseMessage response2 = await client.GetAsync(url, ct);
        Assert.Equal(HttpStatusCode.InternalServerError, response2.StatusCode);
        Assert.Equal(2, _upstreamHandler.GetCount(pkgUrl));
    }

    [Fact]
    public async Task GetNupkg_UpstreamReturnsETag_ETagIsCachedAndReturned()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient client = _factory.CreateClient();

        string pkgUrl = "https://api.nuget.org/v3-flatcontainer/tagged.pkg/1.0.0/tagged.pkg.1.0.0.nupkg";
        _upstreamHandler.MapFactory(pkgUrl, () =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent("tagged-content"u8.ToArray())
                {
                    Headers =
                    {
                        ContentType = new MediaTypeHeaderValue("application/octet-stream"),
                        ContentLength = "tagged-content".Length,
                    },
                },
            };
            resp.Headers.ETag = new EntityTagHeaderValue("\"abc123\"");
            return resp;
        });

        string url = "/v3-flatcontainer/tagged.pkg/1.0.0/tagged.pkg.1.0.0.nupkg";

        HttpResponseMessage response1 = await client.GetAsync(url, ct);
        response1.EnsureSuccessStatusCode();
        Assert.NotNull(response1.Headers.ETag);
        Assert.Equal("\"abc123\"", response1.Headers.ETag.Tag);
        Assert.Equal(1, _upstreamHandler.GetCount(pkgUrl));

        HttpResponseMessage response2 = await client.GetAsync(url, ct);
        response2.EnsureSuccessStatusCode();
        Assert.NotNull(response2.Headers.ETag);
        Assert.Equal("\"abc123\"", response2.Headers.ETag.Tag);
        Assert.Equal(1, _upstreamHandler.GetCount(pkgUrl));
    }

    [Fact]
    public async Task GetNupkg_ContentLengthMismatch_DoesNotCommitToCache()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient client = _factory.CreateClient();

        byte[] realBytes = "short"u8.ToArray();
        string pkgUrl = "https://api.nuget.org/v3-flatcontainer/mismatch.pkg/1.0.0/mismatch.pkg.1.0.0.nupkg";
        _upstreamHandler.MapFactory(pkgUrl, () =>
        {
            var content = new ByteArrayContent(realBytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Headers.ContentLength = 999; // claim much larger than actual
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
            };
        });

        string url = "/v3-flatcontainer/mismatch.pkg/1.0.0/mismatch.pkg.1.0.0.nupkg";

        HttpResponseMessage response1 = await client.GetAsync(url, ct);
        response1.EnsureSuccessStatusCode();
        string body1 = await response1.Content.ReadAsStringAsync(ct);
        Assert.Equal("short", body1);
        Assert.Equal(1, _upstreamHandler.GetCount(pkgUrl));

        HttpResponseMessage response2 = await client.GetAsync(url, ct);
        response2.EnsureSuccessStatusCode();
        Assert.Equal(2, _upstreamHandler.GetCount(pkgUrl));
    }

    [Fact]
    public async Task GetNupkg_ConcurrentRequests_SingleUpstreamFetch()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        int concurrentCount = 8;
        HttpClient client = _factory.CreateClient();
        string url = "/v3-flatcontainer/newtonsoft.json/13.0.3/newtonsoft.json.13.0.3.nupkg";

        var tasks = new Task<HttpResponseMessage>[concurrentCount];
        for (int i = 0; i < concurrentCount; i++)
        {
            tasks[i] = client.GetAsync(url, ct);
        }

        HttpResponseMessage[] responses = await Task.WhenAll(tasks);

        foreach (HttpResponseMessage? response in responses)
        {
            response.EnsureSuccessStatusCode();
            string body = await response.Content.ReadAsStringAsync(ct);
            Assert.Contains("test-nupkg-content-bytes", body, StringComparison.Ordinal);
        }

        Assert.Equal(
            1,
            _upstreamHandler.GetCount("https://api.nuget.org/v3-flatcontainer/newtonsoft.json/13.0.3/newtonsoft.json.13.0.3.nupkg"));
    }

    [Fact]
    public async Task GetNuspec_CachesOnFirstRequestAndServesFromCacheOnSecond()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string nuspecUrl = "https://api.nuget.org/v3-flatcontainer/newtonsoft.json/13.0.3/newtonsoft.json.nuspec";
        _upstreamHandler.MapBytes(nuspecUrl, "<nuspec xmlns=\"http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd\"><metadata><id>Newtonsoft.Json</id></metadata></nuspec>"u8.ToArray(), "application/xml");

        HttpClient client = _factory.CreateClient();
        string url = "/v3-flatcontainer/newtonsoft.json/13.0.3/newtonsoft.json.nuspec";

        HttpResponseMessage response1 = await client.GetAsync(url, ct);
        response1.EnsureSuccessStatusCode();
        string body1 = await response1.Content.ReadAsStringAsync(ct);
        Assert.Contains("Newtonsoft.Json", body1, StringComparison.Ordinal);
        Assert.Equal("application/xml", response1.Content.Headers.ContentType?.ToString());
        Assert.Equal(1, _upstreamHandler.GetCount(nuspecUrl));

        HttpResponseMessage response2 = await client.GetAsync(url, ct);
        response2.EnsureSuccessStatusCode();
        string body2 = await response2.Content.ReadAsStringAsync(ct);
        Assert.Contains("Newtonsoft.Json", body2, StringComparison.Ordinal);
        Assert.Equal(1, _upstreamHandler.GetCount(nuspecUrl));
    }

    [Fact]
    public async Task GetNupkg_IfNoneMatch_Returns304_WhenMatches()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient client = _factory.CreateClient();

        string pkgUrl = "https://api.nuget.org/v3-flatcontainer/etag.304/1.0.0/etag.304.1.0.0.nupkg";
        _upstreamHandler.MapFactory(pkgUrl, () =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent("etag-body"u8.ToArray())
                {
                    Headers =
                    {
                        ContentType = new MediaTypeHeaderValue("application/octet-stream"),
                        ContentLength = "etag-body".Length,
                    },
                },
            };
            resp.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
            return resp;
        });

        string url = "/v3-flatcontainer/etag.304/1.0.0/etag.304.1.0.0.nupkg";

        HttpResponseMessage response1 = await client.GetAsync(url, ct);
        response1.EnsureSuccessStatusCode();
        string body1 = await response1.Content.ReadAsStringAsync(ct);
        Assert.Equal("etag-body", body1);
        Assert.Equal(1, _upstreamHandler.GetCount(pkgUrl));

        var request2 = new HttpRequestMessage(HttpMethod.Get, url);
        request2.Headers.TryAddWithoutValidation("If-None-Match", "\"v1\"");
        HttpResponseMessage response2 = await client.SendAsync(request2, ct);
        Assert.Equal(HttpStatusCode.NotModified, response2.StatusCode);
        Assert.Equal("\"v1\"", response2.Headers.ETag?.Tag);
        string body2 = await response2.Content.ReadAsStringAsync(ct);
        Assert.Empty(body2);
        Assert.Equal(1, _upstreamHandler.GetCount(pkgUrl));
    }

    [Fact]
    public async Task GetNupkg_IfNoneMatch_Returns200_WhenMismatch()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient client = _factory.CreateClient();

        string pkgUrl = "https://api.nuget.org/v3-flatcontainer/etag.200/1.0.0/etag.200.1.0.0.nupkg";
        _upstreamHandler.MapFactory(pkgUrl, () =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent("etag-body"u8.ToArray())
                {
                    Headers =
                    {
                        ContentType = new MediaTypeHeaderValue("application/octet-stream"),
                        ContentLength = "etag-body".Length,
                    },
                },
            };
            resp.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
            return resp;
        });

        string url = "/v3-flatcontainer/etag.200/1.0.0/etag.200.1.0.0.nupkg";

        HttpResponseMessage response1 = await client.GetAsync(url, ct);
        response1.EnsureSuccessStatusCode();
        Assert.Equal(1, _upstreamHandler.GetCount(pkgUrl));

        var request2 = new HttpRequestMessage(HttpMethod.Get, url);
        request2.Headers.TryAddWithoutValidation("If-None-Match", "\"different\"");
        HttpResponseMessage response2 = await client.SendAsync(request2, ct);
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);
        string body2 = await response2.Content.ReadAsStringAsync(ct);
        Assert.Equal("etag-body", body2);
        Assert.Equal(1, _upstreamHandler.GetCount(pkgUrl));
    }

    [Fact]
    public async Task HeadNupkg_IfNoneMatch_Returns304_WhenMatches()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient client = _factory.CreateClient();

        string pkgUrl = "https://api.nuget.org/v3-flatcontainer/etag.head/1.0.0/etag.head.1.0.0.nupkg";
        _upstreamHandler.MapFactory(pkgUrl, () =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent("head-body"u8.ToArray())
                {
                    Headers =
                    {
                        ContentType = new MediaTypeHeaderValue("application/octet-stream"),
                        ContentLength = "head-body".Length,
                    },
                },
            };
            resp.Headers.ETag = new EntityTagHeaderValue("\"head-v1\"");
            return resp;
        });

        string url = "/v3-flatcontainer/etag.head/1.0.0/etag.head.1.0.0.nupkg";

        await client.GetAsync(url, ct);
        Assert.Equal(1, _upstreamHandler.GetCount(pkgUrl));

        var request2 = new HttpRequestMessage(HttpMethod.Head, url);
        request2.Headers.TryAddWithoutValidation("If-None-Match", "\"head-v1\"");
        HttpResponseMessage response2 = await client.SendAsync(request2, ct);
        Assert.Equal(HttpStatusCode.NotModified, response2.StatusCode);
        string body2 = await response2.Content.ReadAsStringAsync(ct);
        Assert.Empty(body2);
        Assert.Equal(1, _upstreamHandler.GetCount(pkgUrl));
    }

    // When Mirror:Cache:Enabled is false, the negative cache must not activate even if
    // Mirror:Cache:NegativeCache:Enabled is true. Both upstream requests must reach the origin.
    [Fact]
    public async Task GetNupkg_Upstream404_NegativeCacheDisabledByGlobalCacheFlag()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        string pkgUrl = "https://api.nuget.org/v3-flatcontainer/missing.pkg/1.0.0/missing.pkg.1.0.0.nupkg";

        var stubHandler = new StubUpstreamHandler();
        stubHandler.MapJson("https://api.nuget.org/v3/index.json", TestFixtures.NuGetOrgServiceIndex);
        stubHandler.MapFactory(pkgUrl, () => new HttpResponseMessage(HttpStatusCode.NotFound));

        await using var factory = new NuGetMirrorWebApplicationFactory(
            _testOutputHelper,
            builder => builder.UseSetting("Mirror:Cache:Enabled", "false"),
            services =>
            {
                services.AddHttpClient("upstream-buffered")
                    .ConfigurePrimaryHttpMessageHandler(() => stubHandler);
                services.AddHttpClient("upstream-stream")
                    .ConfigurePrimaryHttpMessageHandler(() => stubHandler);
            });

        HttpClient client = factory.CreateClient();
        string url = "/v3-flatcontainer/missing.pkg/1.0.0/missing.pkg.1.0.0.nupkg";

        HttpResponseMessage response1 = await client.GetAsync(url, ct);
        Assert.Equal(HttpStatusCode.NotFound, response1.StatusCode);
        Assert.Equal(1, stubHandler.GetCount(pkgUrl));

        // Global cache flag is off, so the negative cache must not have stored the first
        // 404. The second request must reach upstream again.
        HttpResponseMessage response2 = await client.GetAsync(url, ct);
        Assert.Equal(HttpStatusCode.NotFound, response2.StatusCode);
        Assert.Equal(2, stubHandler.GetCount(pkgUrl));
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        _upstreamHandler.Dispose();

        try
        {
            if (Directory.Exists(_cacheDir))
            {
                Directory.Delete(_cacheDir, recursive: true);
            }
        }
        catch
        {
        }
    }
}
