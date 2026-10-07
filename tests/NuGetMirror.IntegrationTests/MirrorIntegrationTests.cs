using System.Net;
using System.Net.Http.Headers;

using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

using NuGetMirror.TestKit;

namespace NuGetMirror.IntegrationTests;

public sealed class MirrorIntegrationTests(ITestOutputHelper testOutputHelper)
{
    private NuGetMirrorWebApplicationFactory CreateFactory(
        StubUpstreamHandler stubHandler,
        Action<IWebHostBuilder>? configureWebHost = null)
    {
        return new NuGetMirrorWebApplicationFactory(testOutputHelper,
            configureWebHost,
            services =>
            {
                services.AddHttpClient("upstream-buffered")
                    .ConfigurePrimaryHttpMessageHandler(() => stubHandler);
                services.AddHttpClient("upstream-stream")
                    .ConfigurePrimaryHttpMessageHandler(() => stubHandler);
            });
    }

    [Fact]
    public async Task GetIndex_ReturnsRewrittenServiceIndex()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        using var stubHandler = new StubUpstreamHandler();
        stubHandler.MapJson("https://api.nuget.org/v3/index.json", TestFixtures.NuGetOrgServiceIndex);
        stubHandler.MapJson(
            "https://api.nuget.org/v3-flatcontainer/newtonsoft.json/index.json",
            """{"versions":["12.0.3","13.0.1","13.0.3"]}""");

        await using NuGetMirrorWebApplicationFactory factory = CreateFactory(stubHandler);

        HttpClient client = factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/v3/index.json", cancellationToken);

        response.EnsureSuccessStatusCode();
        string body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Contains("http://localhost/v3-flatcontainer/", body, StringComparison.Ordinal);
        Assert.Contains("http://localhost/v3/registration-semver2/", body, StringComparison.Ordinal);
        Assert.Contains("http://localhost/v3/registration-semver1/", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetFlatContainer_ReturnsVersionsList()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        using var stubHandler = new StubUpstreamHandler();
        stubHandler.MapJson("https://api.nuget.org/v3/index.json", TestFixtures.NuGetOrgServiceIndex);
        stubHandler.MapJson(
            "https://api.nuget.org/v3-flatcontainer/newtonsoft.json/index.json",
            """{"versions":["12.0.3","13.0.1","13.0.3"]}""");

        await using NuGetMirrorWebApplicationFactory factory = CreateFactory(stubHandler);

        HttpClient client = factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/v3-flatcontainer/newtonsoft.json/index.json", cancellationToken);

        response.EnsureSuccessStatusCode();
        string body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Contains("13.0.3", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HeadFlatContainer_ReturnsHeadersOnly()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        using var stubHandler = new StubUpstreamHandler();
        stubHandler.MapJson("https://api.nuget.org/v3/index.json", TestFixtures.NuGetOrgServiceIndex);
        stubHandler.MapBytes(
            "https://api.nuget.org/v3-flatcontainer/newtonsoft.json/13.0.3/newtonsoft.json.13.0.3.nupkg",
            "fake-nupkg"u8.ToArray(),
            "application/octet-stream");

        await using NuGetMirrorWebApplicationFactory factory = CreateFactory(stubHandler);

        HttpClient client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Head, "/v3-flatcontainer/newtonsoft.json/13.0.3/newtonsoft.json.13.0.3.nupkg");
        HttpResponseMessage response = await client.SendAsync(request, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetRegistration_ReturnsRewrittenUrls()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        using var stubHandler = new StubUpstreamHandler();
        stubHandler.MapJson("https://api.nuget.org/v3/index.json", TestFixtures.NuGetOrgServiceIndex);
        stubHandler.MapJson(
            "https://api.nuget.org/v3/registration5-gz-semver2/newtonsoft.json/index.json",
            """{"@id":"https://api.nuget.org/v3/registration5-gz-semver2/newtonsoft.json/index.json","items":[{"packageContent":"https://api.nuget.org/v3-flatcontainer/newtonsoft.json/13.0.3/newtonsoft.json.13.0.3.nupkg"}]}""");

        await using NuGetMirrorWebApplicationFactory factory = CreateFactory(stubHandler);

        HttpClient client = factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/v3/registration-semver2/newtonsoft.json/index.json", cancellationToken);

        response.EnsureSuccessStatusCode();
        string body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Contains("/v3-flatcontainer/newtonsoft.json/13.0.3/", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetSearch_ReturnsRewrittenUrls()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        using var stubHandler = new StubUpstreamHandler();
        stubHandler.MapJson("https://api.nuget.org/v3/index.json", TestFixtures.NuGetOrgServiceIndex);
        stubHandler.MapJson(
            "https://azuresearch-usnc.nuget.org/query?q=newtonsoft.json",
            """{"totalHits":1,"data":[{"@id":"https://api.nuget.org/v3/registration5-gz-semver2/newtonsoft.json/index.json","registration":"https://api.nuget.org/v3/registration5-gz-semver2/newtonsoft.json/index.json","id":"Newtonsoft.Json","version":"13.0.3","iconUrl":"https://api.nuget.org/v3-flatcontainer/newtonsoft.json/13.0.3/icon","versions":[{"version":"13.0.3","@id":"https://api.nuget.org/v3/registration5-gz-semver2/newtonsoft.json/13.0.3.json"}]}]}""");

        await using NuGetMirrorWebApplicationFactory factory = CreateFactory(stubHandler);

        HttpClient client = factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/v3/search?q=newtonsoft.json", cancellationToken);

        response.EnsureSuccessStatusCode();
        string body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.DoesNotContain("api.nuget.org", body, StringComparison.Ordinal);
        Assert.Contains("http://localhost/v3/registration-semver2/newtonsoft.json/index.json", body, StringComparison.Ordinal);
        Assert.Contains("http://localhost/v3-flatcontainer/newtonsoft.json/13.0.3/icon", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetAutocomplete_ReturnsUpstreamPayload()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        using var stubHandler = new StubUpstreamHandler();
        stubHandler.MapJson("https://api.nuget.org/v3/index.json", TestFixtures.NuGetOrgServiceIndex);
        stubHandler.MapJson(
            "https://azuresearch-usnc.nuget.org/autocomplete?q=newt",
            """{"totalHits":1,"data":["Newtonsoft.Json"]}""");

        await using NuGetMirrorWebApplicationFactory factory = CreateFactory(stubHandler);

        HttpClient client = factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/v3/autocomplete?q=newt", cancellationToken);

        response.EnsureSuccessStatusCode();
        string body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Contains("Newtonsoft.Json", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProxyPassesThrough_404FromUpstream()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        using var stubHandler = new StubUpstreamHandler();
        stubHandler.MapJson("https://api.nuget.org/v3/index.json", TestFixtures.NuGetOrgServiceIndex);

        await using NuGetMirrorWebApplicationFactory factory = CreateFactory(stubHandler);

        HttpClient client = factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/v3-flatcontainer/nonexistent.package/index.json", cancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetRegistrationGzSemver1_ReturnsRewrittenUrls()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        using var stubHandler = new StubUpstreamHandler();
        stubHandler.MapJson("https://api.nuget.org/v3/index.json", TestFixtures.NuGetOrgServiceIndex);
        stubHandler.MapJson(
            "https://api.nuget.org/v3/registration5-gz-semver1/newtonsoft.json/index.json",
            """{"@id":"https://api.nuget.org/v3/registration5-gz-semver1/newtonsoft.json/index.json","items":[{"packageContent":"https://api.nuget.org/v3-flatcontainer/newtonsoft.json/13.0.3/newtonsoft.json.13.0.3.nupkg"}]}""");

        await using NuGetMirrorWebApplicationFactory factory = CreateFactory(stubHandler);

        HttpClient client = factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/v3/registration-gz-semver1/newtonsoft.json/index.json", cancellationToken);

        response.EnsureSuccessStatusCode();
        string body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Contains("/v3-flatcontainer/newtonsoft.json/13.0.3/", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetRegistrationSemver1_ReturnsRewrittenUrls()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        using var stubHandler = new StubUpstreamHandler();
        stubHandler.MapJson("https://api.nuget.org/v3/index.json", TestFixtures.NuGetOrgServiceIndex);
        stubHandler.MapJson(
            "https://api.nuget.org/v3/registration5/newtonsoft.json/index.json",
            """{"@id":"https://api.nuget.org/v3/registration5/newtonsoft.json/index.json","items":[{"packageContent":"https://api.nuget.org/v3-flatcontainer/newtonsoft.json/13.0.3/newtonsoft.json.13.0.3.nupkg"}]}""");

        await using NuGetMirrorWebApplicationFactory factory = CreateFactory(stubHandler);

        HttpClient client = factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/v3/registration-semver1/newtonsoft.json/index.json", cancellationToken);

        response.EnsureSuccessStatusCode();
        string body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Contains("/v3-flatcontainer/newtonsoft.json/13.0.3/", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetNuspec_ReturnsCorrectContentType()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        using var stubHandler = new StubUpstreamHandler();
        stubHandler.MapJson("https://api.nuget.org/v3/index.json", TestFixtures.NuGetOrgServiceIndex);
        stubHandler.MapBytes(
            "https://api.nuget.org/v3-flatcontainer/newtonsoft.json/13.0.3/newtonsoft.json.nuspec",
            "<package><metadata><id>Newtonsoft.Json</id></metadata></package>"u8.ToArray(),
            "application/xml");

        await using NuGetMirrorWebApplicationFactory factory = CreateFactory(stubHandler);

        HttpClient client = factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/v3-flatcontainer/newtonsoft.json/13.0.3/newtonsoft.json.nuspec", cancellationToken);

        response.EnsureSuccessStatusCode();
        string body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Contains("Newtonsoft.Json", body, StringComparison.Ordinal);
        Assert.StartsWith("application/xml", response.Content.Headers.ContentType?.ToString());
    }

    [Fact]
    public async Task GetReadme_ReturnsContent()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string tempDir = Path.Combine(Path.GetTempPath(), "readme-test-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);

        try
        {
            using var stubHandler = new StubUpstreamHandler();
            stubHandler.MapJson("https://api.nuget.org/v3/index.json", TestFixtures.NuGetOrgServiceIndex);
            stubHandler.MapBytes(
                "https://api.nuget.org/v3-flatcontainer/newtonsoft.json/13.0.3/readme",
                "# Newtonsoft.Json README"u8.ToArray(),
                "text/markdown");

            await using NuGetMirrorWebApplicationFactory factory = CreateFactory(stubHandler,
                builder => builder.UseSetting("Mirror:Cache:FileSystem:Directory", tempDir));

            HttpClient client = factory.CreateClient();
            HttpResponseMessage response = await client.GetAsync("/v3-flatcontainer/newtonsoft.json/13.0.3/readme", cancellationToken);

            response.EnsureSuccessStatusCode();
            string body = await response.Content.ReadAsStringAsync(cancellationToken);

            Assert.Contains("Newtonsoft.Json README", body, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task GetReadme_CachesContent()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string tempDir = Path.Combine(Path.GetTempPath(), "readme-cache-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);

        try
        {
            string readmeUrl = "https://api.nuget.org/v3-flatcontainer/newtonsoft.json/13.0.3/readme";

            using var stubHandler = new StubUpstreamHandler();
            stubHandler.MapJson("https://api.nuget.org/v3/index.json", TestFixtures.NuGetOrgServiceIndex);
            stubHandler.MapBytes(readmeUrl, "# Newtonsoft.Json README"u8.ToArray(), "text/markdown");

            await using NuGetMirrorWebApplicationFactory factory = CreateFactory(stubHandler,
                builder => builder.UseSetting("Mirror:Cache:FileSystem:Directory", tempDir));

            HttpClient client = factory.CreateClient();

            HttpResponseMessage response1 = await client.GetAsync("/v3-flatcontainer/newtonsoft.json/13.0.3/readme", cancellationToken);
            response1.EnsureSuccessStatusCode();
            string body1 = await response1.Content.ReadAsStringAsync(cancellationToken);
            Assert.Contains("Newtonsoft.Json README", body1, StringComparison.Ordinal);
            Assert.Equal(1, stubHandler.GetCount(readmeUrl));

            HttpResponseMessage response2 = await client.GetAsync("/v3-flatcontainer/newtonsoft.json/13.0.3/readme", cancellationToken);
            response2.EnsureSuccessStatusCode();
            string body2 = await response2.Content.ReadAsStringAsync(cancellationToken);
            Assert.Contains("Newtonsoft.Json README", body2, StringComparison.Ordinal);
            Assert.Equal(1, stubHandler.GetCount(readmeUrl));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task GetReadme_ProxiesWhenCacheDisabled()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        string readmeUrl = "https://api.nuget.org/v3-flatcontainer/newtonsoft.json/13.0.3/readme";

        using var stubHandler = new StubUpstreamHandler();
        stubHandler.MapJson("https://api.nuget.org/v3/index.json", TestFixtures.NuGetOrgServiceIndex);
        stubHandler.MapBytes(readmeUrl, "# Newtonsoft.Json README"u8.ToArray(), "text/markdown");

        await using NuGetMirrorWebApplicationFactory factory = CreateFactory(stubHandler,
            builder => builder.UseSetting("Mirror:Cache:Enabled", "false"));

        HttpClient client = factory.CreateClient();

        HttpResponseMessage response1 = await client.GetAsync("/v3-flatcontainer/newtonsoft.json/13.0.3/readme", cancellationToken);
        response1.EnsureSuccessStatusCode();

        HttpResponseMessage response2 = await client.GetAsync("/v3-flatcontainer/newtonsoft.json/13.0.3/readme", cancellationToken);
        response2.EnsureSuccessStatusCode();

        Assert.Equal(2, stubHandler.GetCount(readmeUrl));
    }

    [Fact]
    public async Task HeadReadme_ReturnsHeadersOnly()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string tempDir = Path.Combine(Path.GetTempPath(), "readme-head-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);

        try
        {
            using var stubHandler = new StubUpstreamHandler();
            stubHandler.MapJson("https://api.nuget.org/v3/index.json", TestFixtures.NuGetOrgServiceIndex);
            stubHandler.MapBytes(
                "https://api.nuget.org/v3-flatcontainer/newtonsoft.json/13.0.3/readme",
                "readme content"u8.ToArray(),
                "text/markdown");

            await using NuGetMirrorWebApplicationFactory factory = CreateFactory(stubHandler,
                builder => builder.UseSetting("Mirror:Cache:FileSystem:Directory", tempDir));

            HttpClient client = factory.CreateClient();
            var request = new HttpRequestMessage(HttpMethod.Head, "/v3-flatcontainer/newtonsoft.json/13.0.3/readme");
            HttpResponseMessage response = await client.SendAsync(request, cancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(14, response.Content.Headers.ContentLength);
            Assert.StartsWith("text/markdown", response.Content.Headers.ContentType?.ToString());

            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            Assert.Empty(body);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task GetReadme_ServesStaleOnUpstreamFailure()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string tempDir = Path.Combine(Path.GetTempPath(), "readme-stale-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);

        try
        {
            string readmeUrl = "https://api.nuget.org/v3-flatcontainer/newtonsoft.json/13.0.3/readme";
            byte[] readmeContent = "# Newtonsoft.Json README"u8.ToArray();

            int callCount = 0;
            using var stubHandler = new StubUpstreamHandler();
            stubHandler.MapJson("https://api.nuget.org/v3/index.json", TestFixtures.NuGetOrgServiceIndex);
            stubHandler.MapFactory(readmeUrl, () =>
            {
                callCount++;
                if (callCount == 1)
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(readmeContent)
                        {
                            Headers = { ContentType = new MediaTypeHeaderValue("text/markdown") },
                        },
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            });

            await using NuGetMirrorWebApplicationFactory factory = CreateFactory(stubHandler,
                builder =>
                {
                    builder.UseSetting("Mirror:Cache:FileSystem:Directory", tempDir);
                    builder.UseSetting("Mirror:Cache:Readme:CacheTtl", "00:00:00");
                });

            HttpClient client = factory.CreateClient();

            HttpResponseMessage response1 = await client.GetAsync("/v3-flatcontainer/newtonsoft.json/13.0.3/readme", cancellationToken);
            response1.EnsureSuccessStatusCode();
            string body1 = await response1.Content.ReadAsStringAsync(cancellationToken);
            Assert.Contains("Newtonsoft.Json README", body1, StringComparison.Ordinal);
            Assert.Equal(1, callCount);

            HttpResponseMessage response2 = await client.GetAsync("/v3-flatcontainer/newtonsoft.json/13.0.3/readme", cancellationToken);
            response2.EnsureSuccessStatusCode();
            string body2 = await response2.Content.ReadAsStringAsync(cancellationToken);
            Assert.Contains("Newtonsoft.Json README", body2, StringComparison.Ordinal);
            Assert.Equal(2, callCount);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task GetReadme_RevalidatesWith304()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string tempDir = Path.Combine(Path.GetTempPath(), "readme-304-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);

        try
        {
            string readmeUrl = "https://api.nuget.org/v3-flatcontainer/newtonsoft.json/13.0.3/readme";
            byte[] readmeContent = "# Newtonsoft.Json README"u8.ToArray();

            int callCount = 0;
            using var stubHandler = new StubUpstreamHandler();
            stubHandler.MapJson("https://api.nuget.org/v3/index.json", TestFixtures.NuGetOrgServiceIndex);
            stubHandler.MapFactory(readmeUrl, () =>
            {
                callCount++;
                if (callCount == 1)
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(readmeContent)
                        {
                            Headers = { ContentType = new MediaTypeHeaderValue("text/markdown") },
                        },
                        Headers = { ETag = new EntityTagHeaderValue("\"readme-v1\"") },
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotModified);
            });

            await using NuGetMirrorWebApplicationFactory factory = CreateFactory(stubHandler,
                builder =>
                {
                    builder.UseSetting("Mirror:Cache:FileSystem:Directory", tempDir);
                    builder.UseSetting("Mirror:Cache:Readme:CacheTtl", "00:00:00");
                });

            HttpClient client = factory.CreateClient();

            HttpResponseMessage response1 = await client.GetAsync("/v3-flatcontainer/newtonsoft.json/13.0.3/readme", cancellationToken);
            response1.EnsureSuccessStatusCode();
            string body1 = await response1.Content.ReadAsStringAsync(cancellationToken);
            Assert.Contains("Newtonsoft.Json README", body1, StringComparison.Ordinal);
            Assert.Equal(1, callCount);

            HttpResponseMessage response2 = await client.GetAsync("/v3-flatcontainer/newtonsoft.json/13.0.3/readme", cancellationToken);
            response2.EnsureSuccessStatusCode();
            string body2 = await response2.Content.ReadAsStringAsync(cancellationToken);
            Assert.Contains("Newtonsoft.Json README", body2, StringComparison.Ordinal);
            Assert.Equal(2, callCount);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task GetRegistration_CacheHit_DoesNotCallUpstreamAgain()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string tempDir = Path.Combine(Path.GetTempPath(), "reg-cachehit-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);

        try
        {
            string regUrl = "https://api.nuget.org/v3/registration5-gz-semver2/newtonsoft.json/index.json";
            string regContent = """{"@id":"https://api.nuget.org/v3/registration5-gz-semver2/newtonsoft.json/index.json","items":[{"packageContent":"https://api.nuget.org/v3-flatcontainer/newtonsoft.json/13.0.3/newtonsoft.json.13.0.3.nupkg"}]}""";

            int callCount = 0;
            using var stubHandler = new StubUpstreamHandler();
            stubHandler.MapJson("https://api.nuget.org/v3/index.json", TestFixtures.NuGetOrgServiceIndex);
            stubHandler.MapFactory(regUrl, () =>
            {
                Interlocked.Increment(ref callCount);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(regContent, System.Text.Encoding.UTF8, "application/json"),
                };
            });

            await using NuGetMirrorWebApplicationFactory factory = CreateFactory(stubHandler,
                builder =>
                {
                    builder.UseSetting("Mirror:Cache:FileSystem:Directory", tempDir);
                    builder.UseSetting("Mirror:Cache:Registration:CacheTtl", "00:15:00");
                });

            HttpClient client = factory.CreateClient();

            HttpResponseMessage response1 = await client.GetAsync("/v3/registration-semver2/newtonsoft.json/index.json", cancellationToken);
            response1.EnsureSuccessStatusCode();
            string body1 = await response1.Content.ReadAsStringAsync(cancellationToken);
            Assert.DoesNotContain("api.nuget.org", body1, StringComparison.Ordinal);
            Assert.Equal(1, Volatile.Read(ref callCount));

            HttpResponseMessage response2 = await client.GetAsync("/v3/registration-semver2/newtonsoft.json/index.json", cancellationToken);
            response2.EnsureSuccessStatusCode();
            string body2 = await response2.Content.ReadAsStringAsync(cancellationToken);
            Assert.DoesNotContain("api.nuget.org", body2, StringComparison.Ordinal);
            Assert.Equal(1, Volatile.Read(ref callCount));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
