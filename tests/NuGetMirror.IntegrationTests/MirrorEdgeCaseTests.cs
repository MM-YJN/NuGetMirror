using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using NuGetMirror.TestKit;

namespace NuGetMirror.IntegrationTests;

public sealed class MirrorEdgeCaseTests : IDisposable
{
    private readonly ITestOutputHelper _testOutputHelper;
    private readonly StubUpstreamHandler _upstreamHandler;

    public MirrorEdgeCaseTests(ITestOutputHelper testOutputHelper)
    {
        _testOutputHelper = testOutputHelper;
        _upstreamHandler = new StubUpstreamHandler();
        _upstreamHandler
            .MapJson("https://api.nuget.org/v3/index.json", TestFixtures.NuGetOrgServiceIndex)
            .MapJson(
                "https://api.nuget.org/v3-flatcontainer/newtonsoft.json/index.json",
                """{"versions":["12.0.3","13.0.1","13.0.3"]}""")
            .MapBytes(
                "https://api.nuget.org/v3-flatcontainer/newtonsoft.json/13.0.3/newtonsoft.json.13.0.3.nupkg",
                "live-content"u8.ToArray(),
                "application/octet-stream");
    }

    public void Dispose() => _upstreamHandler.Dispose();

    private NuGetMirrorWebApplicationFactory CreatePublicBaseUrlFactory()
    {
        return new NuGetMirrorWebApplicationFactory(_testOutputHelper, builder =>
        {
            builder.UseSetting("Mirror:Cache:Enabled", "false");
            builder.UseSetting("Mirror:PublicBaseUrl", "https://mirror.example.com");
            builder.ConfigureServices(services =>
            {
                services.AddHttpClient("upstream-buffered")
                    .ConfigurePrimaryHttpMessageHandler(() => _upstreamHandler);
                services.AddHttpClient("upstream-stream")
                    .ConfigurePrimaryHttpMessageHandler(() => _upstreamHandler);
            });
        });
    }

    private NuGetMirrorWebApplicationFactory CreateLiveStreamFactory()
    {
        return new NuGetMirrorWebApplicationFactory(_testOutputHelper, builder =>
        {
            builder.UseSetting("Mirror:Cache:Enabled", "false");
            builder.ConfigureServices(services =>
            {
                services.AddHttpClient("upstream-buffered")
                    .ConfigurePrimaryHttpMessageHandler(() => _upstreamHandler);
                services.AddHttpClient("upstream-stream")
                    .ConfigurePrimaryHttpMessageHandler(() => _upstreamHandler);
            });
        });
    }

    private NuGetMirrorWebApplicationFactory CreateCacheEnabledFactory(StubUpstreamHandler upstreamHandler)
    {
        return new NuGetMirrorWebApplicationFactory(_testOutputHelper, builder =>
        {
            builder.UseSetting("Mirror:Cache:Enabled", "true");
            builder.ConfigureServices(services =>
            {
                services.AddHttpClient("upstream-buffered")
                    .ConfigurePrimaryHttpMessageHandler(() => upstreamHandler);
                services.AddHttpClient("upstream-stream")
                    .ConfigurePrimaryHttpMessageHandler(() => upstreamHandler);
            });
        });
    }

    private NuGetMirrorWebApplicationFactory CreateCacheDisabledFactory(StubUpstreamHandler? upstreamHandler = null)
    {
        StubUpstreamHandler handler = upstreamHandler ?? _upstreamHandler;

        return new NuGetMirrorWebApplicationFactory(_testOutputHelper, builder =>
        {
            builder.UseSetting("Mirror:Cache:Enabled", "false");
            builder.ConfigureServices(services =>
            {
                services.AddHttpClient("upstream-buffered")
                    .ConfigurePrimaryHttpMessageHandler(() => handler);
                services.AddHttpClient("upstream-stream")
                    .ConfigurePrimaryHttpMessageHandler(() => handler);
            });
        });
    }

    [Fact]
    public async Task GetIndex_Returns502_WhenUpstreamIndexFails()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        var failHandler = new StubUpstreamHandler();
        failHandler.MapStatus("https://api.nuget.org/v3/index.json", HttpStatusCode.InternalServerError);

        await using NuGetMirrorWebApplicationFactory failFactory = CreateCacheDisabledFactory(failHandler);

        HttpClient client = failFactory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/v3/index.json", ct);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task GetIndex_UsesPublicBaseUrl()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        await using NuGetMirrorWebApplicationFactory factory = CreatePublicBaseUrlFactory();
        HttpClient client = factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/v3/index.json", ct);

        response.EnsureSuccessStatusCode();
        string body = await response.Content.ReadAsStringAsync(ct);

        Assert.Contains("https://mirror.example.com/v3-flatcontainer/", body, StringComparison.Ordinal);
        Assert.Contains("https://mirror.example.com/v3/registration-semver2/", body, StringComparison.Ordinal);
        Assert.DoesNotContain("http://localhost", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetFlatContainer_ReturnsContent_WhenCacheDisabled()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        await using NuGetMirrorWebApplicationFactory factory = CreateLiveStreamFactory();
        HttpClient client = factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/v3-flatcontainer/newtonsoft.json/13.0.3/newtonsoft.json.13.0.3.nupkg", ct);

        response.EnsureSuccessStatusCode();
        byte[] body = await response.Content.ReadAsByteArrayAsync(ct);
        Assert.Equal("live-content"u8.ToArray(), body);
    }

    [Fact]
    public async Task GetFlatContainer_LiveStream_ForwardsResponseHeaders()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string etag = "\"abc123\"";

        using var handler = new StubUpstreamHandler();
        handler.MapJson("https://api.nuget.org/v3/index.json", TestFixtures.NuGetOrgServiceIndex);
        handler.MapJson(
            "https://api.nuget.org/v3-flatcontainer/newtonsoft.json/index.json",
            """{"versions":["12.0.3","13.0.1","13.0.3"]}""");
        handler.MapFactory(
            "https://api.nuget.org/v3-flatcontainer/newtonsoft.json/13.0.3/newtonsoft.json.13.0.3.nupkg",
            () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent("header-test"u8.ToArray())
                {
                    Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") },
                },
                Headers =
                {
                    ETag = new EntityTagHeaderValue(etag),
                },
            });

        await using NuGetMirrorWebApplicationFactory factory = CreateCacheEnabledFactory(handler);

        HttpClient client = factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/v3-flatcontainer/newtonsoft.json/13.0.3/newtonsoft.json.13.0.3.nupkg", ct);

        response.EnsureSuccessStatusCode();
        byte[] body = await response.Content.ReadAsByteArrayAsync(ct);
        Assert.Equal("header-test"u8.ToArray(), body);
        Assert.Equal(etag, response.Headers.ETag?.Tag);
    }

    private NuGetMirrorWebApplicationFactory CreateAdminEnabledFactory()
    {
        return new NuGetMirrorWebApplicationFactory(_testOutputHelper, builder =>
        {
            builder.UseSetting("Mirror:Admin:Enabled", "true");
            builder.UseSetting("Mirror:Admin:Path", "/admin/stats");
            builder.ConfigureServices(services =>
            {
                services.AddHttpClient("upstream-buffered")
                    .ConfigurePrimaryHttpMessageHandler(() => _upstreamHandler);
                services.AddHttpClient("upstream-stream")
                    .ConfigurePrimaryHttpMessageHandler(() => _upstreamHandler);
            });
        });
    }

    [Fact]
    public async Task GetAdminStats_Enabled_ReturnsJson()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        await using NuGetMirrorWebApplicationFactory factory = CreateAdminEnabledFactory();

        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/admin/stats", ct);
        response.EnsureSuccessStatusCode();
        Assert.Equal("application/json", response.Content.Headers.ContentType?.ToString());

        string body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        Assert.True(root.TryGetProperty("version", out _));
        Assert.True(root.TryGetProperty("uptime", out _));
        Assert.True(root.TryGetProperty("upstream", out JsonElement upstream));
        Assert.True(upstream.TryGetProperty("indexUrl", out _));
        Assert.True(upstream.TryGetProperty("resourceCount", out _));
        Assert.True(root.TryGetProperty("cache", out JsonElement cache));
        Assert.True(cache.TryGetProperty("enabled", out _));
        Assert.True(cache.TryGetProperty("backend", out _));
        Assert.True(root.TryGetProperty("storage", out JsonElement storage));
        Assert.True(storage.TryGetProperty("healthy", out _));
    }

    [Fact]
    public async Task GetAdminStats_Disabled_Returns404()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        await using NuGetMirrorWebApplicationFactory factory = CreateLiveStreamFactory();
        HttpClient client = factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/admin/stats", ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
