using System.Net;
using System.Reflection;

using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using NuGetMirror.Configuration;
using NuGetMirror.Diagnostics;
using NuGetMirror.Diagnostics.HealthChecks;
using NuGetMirror.Discovery;
using NuGetMirror.TestKit;
using NuGetMirror.Upstream;

namespace NuGetMirror.UnitTests;

public sealed class UpstreamHealthCheckTests
{
    private static readonly FieldInfo s_currentField =
        typeof(DiscoveryCache).GetField("_current", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("DiscoveryCache._current field not found.");

    [Fact]
    public async Task ReturnsHealthy_WhenUpstreamIndexIsFresh()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var options = new MirrorOptions();
        IOptions<MirrorOptions> optionsWrapper = Microsoft.Extensions.Options.Options.Create(options);
        var httpClient = new TestHttpClientFactory(new SuccessHandler(TestFixtures.NuGetOrgServiceIndex));
        var client = new UpstreamClient(httpClient, optionsWrapper, NullLogger<UpstreamClient>.Instance, new MirrorMetrics());
        var cache = new DiscoveryCache(client, optionsWrapper, NullLogger<DiscoveryCache>.Instance, new MirrorMetrics(), TimeProvider.System);
        var check = new UpstreamHealthCheck(cache, optionsWrapper, NullLogger<UpstreamHealthCheck>.Instance, TimeProvider.System);

        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext(), ct);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task ReturnsUnhealthy_WhenDiscoveryFailsWithNoSnapshot()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var options = new MirrorOptions();
        IOptions<MirrorOptions> optionsWrapper = Microsoft.Extensions.Options.Options.Create(options);
        var httpClient = new TestHttpClientFactory(new FailingHandler());
        var client = new UpstreamClient(httpClient, optionsWrapper, NullLogger<UpstreamClient>.Instance, new MirrorMetrics());
        var cache = new DiscoveryCache(client, optionsWrapper, NullLogger<DiscoveryCache>.Instance, new MirrorMetrics(), TimeProvider.System);
        var check = new UpstreamHealthCheck(cache, optionsWrapper, NullLogger<UpstreamHealthCheck>.Instance, TimeProvider.System);

        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext(), ct);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task ReturnsDegraded_WhenSnapshotIsStale()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var options = new MirrorOptions { Upstream = { DiscoveryCacheTtl = TimeSpan.Zero } };
        IOptions<MirrorOptions> optionsWrapper = Microsoft.Extensions.Options.Options.Create(options);
        var httpClient = new TestHttpClientFactory(new FailingHandler());
        var client = new UpstreamClient(httpClient, optionsWrapper, NullLogger<UpstreamClient>.Instance, new MirrorMetrics());
        var cache = new DiscoveryCache(client, optionsWrapper, NullLogger<DiscoveryCache>.Instance, new MirrorMetrics(), TimeProvider.System);

        // Seed a stale snapshot so GetAsync does not throw
        var staleSnapshot = new DiscoverySnapshot(
            DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1),
            TestFixtures.NuGetOrgServiceIndex,
            new Dictionary<string, string>(),
            []);
        s_currentField.SetValue(cache, staleSnapshot);

        var check = new UpstreamHealthCheck(cache, optionsWrapper, NullLogger<UpstreamHealthCheck>.Instance, TimeProvider.System);

        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext(), ct);

        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    [Fact]
    public async Task ReturnsDegraded_WhenSnapshotAgedPastTtl()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var options = new MirrorOptions { Upstream = { DiscoveryCacheTtl = TimeSpan.FromMinutes(30) } };
        IOptions<MirrorOptions> optionsWrapper = Microsoft.Extensions.Options.Options.Create(options);

        // First, succeed to seed the cache
        using var successHandler = new SuccessHandler(TestFixtures.NuGetOrgServiceIndex);
        var successClient = new UpstreamClient(new TestHttpClientFactory(successHandler), optionsWrapper, NullLogger<UpstreamClient>.Instance, new MirrorMetrics());
        var cache = new DiscoveryCache(successClient, optionsWrapper, NullLogger<DiscoveryCache>.Instance, new MirrorMetrics(), clock);
        DiscoverySnapshot snapshot = await cache.GetAsync(ct);

        // Advance clock past TTL so GetAsync attempts a refresh
        clock.Advance(TimeSpan.FromMinutes(31));

        // Replace upstream client with a failing one so refresh fails and stale fallback kicks in
        FieldInfo snapshotField = typeof(DiscoveryCache).GetField("_current", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("DiscoveryCache._current field not found.");

        using var failingHandler = new FailingHandler();
        var failingClient = new UpstreamClient(new TestHttpClientFactory(failingHandler), optionsWrapper, NullLogger<UpstreamClient>.Instance, new MirrorMetrics());
        var failingCache = new DiscoveryCache(failingClient, optionsWrapper, NullLogger<DiscoveryCache>.Instance, new MirrorMetrics(), clock);
        snapshotField.SetValue(failingCache, snapshot);

        var check = new UpstreamHealthCheck(failingCache, optionsWrapper, NullLogger<UpstreamHealthCheck>.Instance, clock);
        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext(), ct);

        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    private sealed class SuccessHandler(string responseBody) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody),
            });
        }
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new HttpRequestException("Upstream unreachable");
        }
    }
}
