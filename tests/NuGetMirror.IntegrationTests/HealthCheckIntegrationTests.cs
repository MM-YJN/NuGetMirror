using System.Net;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using NuGetMirror.TestKit;
using NuGetMirror.TestKit.Logger;

namespace NuGetMirror.IntegrationTests;

public sealed class HealthCheckIntegrationTests(ITestOutputHelper testOutputHelper) : IAsyncDisposable
{
    private WebApplicationFactory<Program>? _factory;

    public async ValueTask DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
    }

    [Fact]
    public async Task LiveEndpoint_ReturnsOk_Always()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        _factory = CreateFactory(stub => { });

        HttpClient client = _factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/health/live", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync(ct);
        Assert.Equal("Healthy", body);
    }

    [Fact]
    public async Task ReadyEndpoint_ReturnsOk_WhenUpstreamIsReachable()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        _factory = CreateFactory(stub => stub.MapJson("https://api.nuget.org/v3/index.json", TestFixtures.NuGetOrgServiceIndex));

        HttpClient client = _factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/health/ready", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync(ct);
        Assert.Equal("Healthy", body);
    }

    [Fact]
    public async Task ReadyEndpoint_ReturnsServiceUnavailable_WhenUpstreamFailsWithNoSnapshot()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        _factory = CreateFactory(stub => stub.MapStatus("https://api.nuget.org/v3/index.json", HttpStatusCode.InternalServerError));

        HttpClient client = _factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/health/ready", ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync(ct);
        Assert.StartsWith("Unhealthy", body, StringComparison.Ordinal);
    }

    private WebApplicationFactory<Program> CreateFactory(Action<StubUpstreamHandler> configureStub)
    {
        var stub = new StubUpstreamHandler();
        configureStub(stub);

        return new StubUpstreamWebApplicationFactory(testOutputHelper, stub);
    }

    private sealed class StubUpstreamWebApplicationFactory(ITestOutputHelper testOutputHelper, StubUpstreamHandler stub) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureLogging(loggingBuilder =>
            {
                loggingBuilder.ClearProviders();
                loggingBuilder.AddProvider(new XunitTestOutputLoggerProvider(testOutputHelper));
            });

            builder.ConfigureServices(services =>
            {
                services.AddHttpClient("upstream-buffered")
                    .ConfigurePrimaryHttpMessageHandler(() => stub);
                services.AddHttpClient("upstream-stream")
                    .ConfigurePrimaryHttpMessageHandler(() => stub);
            });
        }
    }
}
