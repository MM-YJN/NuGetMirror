using System.Net;

using Microsoft.Extensions.DependencyInjection;

using NuGetMirror.TestKit;

namespace NuGetMirror.IntegrationTests;

public sealed class BasePathIntegrationTests : IDisposable
{
    private readonly ITestOutputHelper _testOutputHelper;
    private readonly StubUpstreamHandler _upstream;

    public BasePathIntegrationTests(ITestOutputHelper testOutputHelper)
    {
        _testOutputHelper = testOutputHelper;
        _upstream = new StubUpstreamHandler();
        _upstream.MapJson("https://api.nuget.org/v3/index.json", TestFixtures.NuGetOrgServiceIndex);
    }

    public void Dispose()
    {
        _upstream.Dispose();
    }

    private NuGetMirrorWebApplicationFactory CreateBasePathFactory()
    {
        // Mirror with BasePath=/nuget, cache disabled.
        return new NuGetMirrorWebApplicationFactory(_testOutputHelper, builder =>
        {
            builder.UseSetting("Mirror:Cache:Enabled", "false");
            builder.UseSetting("Mirror:BasePath", "/nuget");
            builder.UseSetting("Mirror:PublicBaseUrl", "https://mirror.example.com");
            builder.ConfigureServices(services =>
            {
                services.AddHttpClient("upstream-buffered")
                    .ConfigurePrimaryHttpMessageHandler(() => _upstream);
                services.AddHttpClient("upstream-stream")
                    .ConfigurePrimaryHttpMessageHandler(() => _upstream);
            });
        });
    }

    private NuGetMirrorWebApplicationFactory CreateBasePathWithPublicUrlFactory()
    {
        // Mirror with BasePath=/nuget and an explicit PublicBaseUrl.
        return new NuGetMirrorWebApplicationFactory(_testOutputHelper, builder =>
        {
            builder.UseSetting("Mirror:Cache:Enabled", "false");
            builder.UseSetting("Mirror:BasePath", "/nuget");
            builder.UseSetting("Mirror:PublicBaseUrl", "https://mirror.example.com");
            builder.ConfigureServices(services =>
            {
                services.AddHttpClient("upstream-buffered")
                    .ConfigurePrimaryHttpMessageHandler(() => _upstream);
                services.AddHttpClient("upstream-stream")
                    .ConfigurePrimaryHttpMessageHandler(() => _upstream);
            });
        });
    }

    // ── Route registration ───────────────────────────────────────────────────

    [Fact]
    public async Task GetIndex_AtSubpath_Returns200()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using NuGetMirrorWebApplicationFactory factory = CreateBasePathFactory();
        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/nuget/v3/index.json", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetIndex_AtRoot_Returns404_WhenBasePathConfigured()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using NuGetMirrorWebApplicationFactory factory = CreateBasePathFactory();
        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/v3/index.json", ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetFlatContainer_AtSubpath_Returns200()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        _upstream.MapJson(
            "https://api.nuget.org/v3-flatcontainer/newtonsoft.json/index.json",
            """{"versions":["13.0.3"]}""");

        await using NuGetMirrorWebApplicationFactory factory = CreateBasePathFactory();
        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/nuget/v3-flatcontainer/newtonsoft.json/index.json", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetFlatContainer_AtRoot_Returns404_WhenBasePathConfigured()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using NuGetMirrorWebApplicationFactory factory = CreateBasePathFactory();
        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/v3-flatcontainer/newtonsoft.json/index.json", ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── URL rewriting ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetIndex_RewrittenUrls_ContainSubpath()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using NuGetMirrorWebApplicationFactory factory = CreateBasePathFactory();
        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/nuget/v3/index.json", ct);
        string body = await response.Content.ReadAsStringAsync(ct);

        // All rewritten resource @id values must carry the /nuget prefix.
        Assert.Contains("/nuget/v3-flatcontainer/", body, StringComparison.Ordinal);
        Assert.Contains("/nuget/v3/registration-semver2/", body, StringComparison.Ordinal);

        // Specific upstream @id URLs must have been rewritten — none of these raw upstream
        // base URLs should remain in the body. (The fixture's "comment" field contains a
        // different api.nuget.org path as prose text; those are intentionally not rewritten.)
        Assert.DoesNotContain("https://api.nuget.org/v3-flatcontainer/", body, StringComparison.Ordinal);
        Assert.DoesNotContain("https://api.nuget.org/v3/registration5-gz-semver2/", body, StringComparison.Ordinal);

        // No root-level (subpath-less) mirror URLs should be present in the output.
        Assert.DoesNotContain("http://localhost/v3-flatcontainer/", body, StringComparison.Ordinal);
        Assert.DoesNotContain("http://localhost/v3/registration-semver2/", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetIndex_RewrittenUrls_IncludeSubpath_WhenPublicBaseUrlAndBasePathBothSet()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using NuGetMirrorWebApplicationFactory factory = CreateBasePathWithPublicUrlFactory();
        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/nuget/v3/index.json", ct);
        string body = await response.Content.ReadAsStringAsync(ct);

        Assert.Contains("https://mirror.example.com/nuget/v3-flatcontainer/", body, StringComparison.Ordinal);
        Assert.Contains("https://mirror.example.com/nuget/v3/registration-semver2/", body, StringComparison.Ordinal);
        Assert.DoesNotContain("http://localhost", body, StringComparison.Ordinal);
        Assert.DoesNotContain("https://mirror.example.com/v3-flatcontainer/", body, StringComparison.Ordinal);
    }

    // ── Health endpoints (must stay at root) ─────────────────────────────────

    [Fact]
    public async Task HealthLive_StaysAtRoot_WhenBasePathConfigured()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using NuGetMirrorWebApplicationFactory factory = CreateBasePathFactory();
        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/health/live", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task HealthReady_StaysAtRoot_WhenBasePathConfigured()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using NuGetMirrorWebApplicationFactory factory = CreateBasePathFactory();
        HttpClient client = factory.CreateClient();

        // The ready check also probes upstream reachability; that's fine here since
        // the stub responds to the index URL.
        HttpResponseMessage response = await client.GetAsync("/health/ready", ct);

        // Healthy or degraded — either is fine; just confirm it's reachable at root.
        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Admin endpoint (must move under subpath) ─────────────────────────────

    private NuGetMirrorWebApplicationFactory CreateAdminEnabledBasePathFactory()
    {
        // Mirror with BasePath=/nuget and Admin enabled.
        return new NuGetMirrorWebApplicationFactory(_testOutputHelper, builder =>
        {
            builder.UseSetting("Mirror:Cache:Enabled", "false");
            builder.UseSetting("Mirror:BasePath", "/nuget");
            builder.UseSetting("Mirror:Admin:Enabled", "true");
            builder.ConfigureServices(services =>
            {
                services.AddHttpClient("upstream-buffered")
                    .ConfigurePrimaryHttpMessageHandler(() => _upstream);
                services.AddHttpClient("upstream-stream")
                    .ConfigurePrimaryHttpMessageHandler(() => _upstream);
            });
        });
    }

    [Fact]
    public async Task AdminStats_MovesToSubpath_WhenBasePathConfigured()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        await using NuGetMirrorWebApplicationFactory factory = CreateAdminEnabledBasePathFactory();

        HttpClient client = factory.CreateClient();

        // Admin is now under the subpath.
        HttpResponseMessage subpathResponse = await client.GetAsync("/nuget/admin/stats", ct);
        Assert.Equal(HttpStatusCode.OK, subpathResponse.StatusCode);

        // Root path is gone.
        HttpResponseMessage rootResponse = await client.GetAsync("/admin/stats", ct);
        Assert.Equal(HttpStatusCode.NotFound, rootResponse.StatusCode);
    }

    // ── No base path ─────────────────────────────────────────────────────────

    private NuGetMirrorWebApplicationFactory CreateNoBasePathFactory()
    {
        // Mirror with no BasePath and cache disabled.
        return new NuGetMirrorWebApplicationFactory(_testOutputHelper, builder =>
        {
            builder.UseSetting("Mirror:Cache:Enabled", "false");
            builder.ConfigureServices(services =>
            {
                services.AddHttpClient("upstream-buffered")
                    .ConfigurePrimaryHttpMessageHandler(() => _upstream);
                services.AddHttpClient("upstream-stream")
                    .ConfigurePrimaryHttpMessageHandler(() => _upstream);
            });
        });
    }

    [Fact]
    public async Task GetIndex_AtRoot_Returns200_WhenBasePathNotConfigured()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        await using NuGetMirrorWebApplicationFactory factory = CreateNoBasePathFactory();

        HttpClient client = factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/v3/index.json", ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
