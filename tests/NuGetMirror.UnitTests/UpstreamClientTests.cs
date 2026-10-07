using System.Net;
using System.Net.Http.Headers;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NuGetMirror.Configuration;
using NuGetMirror.Diagnostics;
using NuGetMirror.TestKit;
using NuGetMirror.Upstream;

namespace NuGetMirror.UnitTests;

public sealed class UpstreamClientTests
{
    [Fact]
    public async Task GetAsync_AppliesBearerAuth()
    {
        AuthenticationHeaderValue? capturedHeader = await RunAuthTest(new UpstreamAuthOptions { Scheme = "Bearer", Token = "my-token" });

        Assert.NotNull(capturedHeader);
        Assert.Equal("Bearer", capturedHeader.Scheme);
        Assert.Equal("my-token", capturedHeader.Parameter);
    }

    [Fact]
    public async Task GetAsync_AppliesBasicAuth()
    {
        AuthenticationHeaderValue? capturedHeader = await RunAuthTest(new UpstreamAuthOptions { Scheme = "Basic", Token = "dXNlcjpwYXNz" });

        Assert.NotNull(capturedHeader);
        Assert.Equal("Basic", capturedHeader.Scheme);
        Assert.Equal("dXNlcjpwYXNz", capturedHeader.Parameter);
    }

    [Fact]
    public async Task GetAsync_AppliesCustomHeaderAuth()
    {
        (string? capturedKey, string? capturedValue) = await RunCustomHeaderAuthTest(
            new UpstreamAuthOptions { Scheme = "Header", Token = "secret-key", HeaderName = "X-NuGet-ApiKey" });

        Assert.Equal("X-NuGet-ApiKey", capturedKey);
        Assert.Equal("secret-key", capturedValue);
    }

    [Fact]
    public async Task GetAsync_NoAuthWhenNull()
    {
        AuthenticationHeaderValue? capturedAuth = await RunAuthTest(null);

        Assert.Null(capturedAuth);
    }

    private static async Task<AuthenticationHeaderValue?> RunAuthTest(UpstreamAuthOptions? auth)
    {
        var options = new MirrorOptions { Upstream = { Auth = auth } };
        IOptions<MirrorOptions> optionsWrapper = Microsoft.Extensions.Options.Options.Create(options);
        using var handler = new CapturingHandler();
        var factory = new TestHttpClientFactory(handler);
        var client = new UpstreamClient(factory, optionsWrapper, NullLogger<UpstreamClient>.Instance, new MirrorMetrics());

        await client.GetAsync(new Uri("https://api.nuget.org/v3/index.json"), stream: false, TestContext.Current.CancellationToken);

        return handler.CapturedRequest?.Headers.Authorization;
    }

    private static async Task<(string? Key, string? Value)> RunCustomHeaderAuthTest(UpstreamAuthOptions auth)
    {
        var options = new MirrorOptions { Upstream = { Auth = auth } };
        IOptions<MirrorOptions> optionsWrapper = Microsoft.Extensions.Options.Options.Create(options);
        using var handler = new CapturingHandler();
        var factory = new TestHttpClientFactory(handler);
        var client = new UpstreamClient(factory, optionsWrapper, NullLogger<UpstreamClient>.Instance, new MirrorMetrics());

        await client.GetAsync(new Uri("https://api.nuget.org/v3/index.json"), stream: false, TestContext.Current.CancellationToken);

        HttpRequestMessage? request = handler.CapturedRequest;
        string headerName = auth.HeaderName ?? throw new InvalidOperationException("HeaderName must be set for custom header auth test.");
        IEnumerable<string>? values = request?.Headers.GetValues(headerName);
        return (headerName, values?.FirstOrDefault());
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? CapturedRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
