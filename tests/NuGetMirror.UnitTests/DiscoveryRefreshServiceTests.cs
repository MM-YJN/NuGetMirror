using System.Reflection;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NuGetMirror.Configuration;
using NuGetMirror.Diagnostics;
using NuGetMirror.Discovery;
using NuGetMirror.TestKit;
using NuGetMirror.Upstream;

namespace NuGetMirror.UnitTests;

public sealed class DiscoveryRefreshServiceTests
{
    [Fact]
    public async Task RefreshAsync_ForcesRefetch_WhenSnapshotIsFresh()
    {
        var handler = new StubUpstreamHandler();
        handler.MapJson("https://api.nuget.org/v3/index.json", TestFixtures.NuGetOrgServiceIndex);

        var httpClient = new TestHttpClientFactory(handler);
        IOptions<MirrorOptions> optionsWrapper = Microsoft.Extensions.Options.Options.Create(new MirrorOptions());

        var client = new UpstreamClient(httpClient, optionsWrapper, NullLogger<UpstreamClient>.Instance, new MirrorMetrics());
        var cache = new DiscoveryCache(client, optionsWrapper, NullLogger<DiscoveryCache>.Instance, new MirrorMetrics(), TimeProvider.System);
        await cache.GetAsync(CancellationToken.None);

        // Change the upstream response to verify a fresh response is fetched
        handler.MapJson("https://api.nuget.org/v3/index.json", """{"version":"3.0.0","resources":[]}""");

        await cache.RefreshAsync(TestContext.Current.CancellationToken);

        DiscoverySnapshot snapshot = cache.CurrentSnapshot;
        Assert.NotNull(snapshot);
        Assert.Contains("\"resources\":[]", snapshot.RawUpstreamIndexJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshAsync_StaleFallback_OnFailure()
    {
        var handler = new StubUpstreamHandler();
        handler.MapJson("https://api.nuget.org/v3/index.json", TestFixtures.NuGetOrgServiceIndex);

        var httpClient = new TestHttpClientFactory(handler);
        IOptions<MirrorOptions> optionsWrapper = Microsoft.Extensions.Options.Options.Create(new MirrorOptions());

        var client = new UpstreamClient(httpClient, optionsWrapper, NullLogger<UpstreamClient>.Instance, new MirrorMetrics());
        var cache = new DiscoveryCache(client, optionsWrapper, NullLogger<DiscoveryCache>.Instance, new MirrorMetrics(), TimeProvider.System);
        DiscoverySnapshot initial = await cache.GetAsync(CancellationToken.None);

        // Make upstream fail
        handler.MapStatus("https://api.nuget.org/v3/index.json", System.Net.HttpStatusCode.InternalServerError);

        await cache.RefreshAsync(TestContext.Current.CancellationToken);

        DiscoverySnapshot snapshot = cache.CurrentSnapshot;
        Assert.NotNull(snapshot);
        Assert.Same(initial, snapshot);
    }

    [Fact]
    public async Task Service_WarmUp_RefreshesOnStartup()
    {
        var handler = new StubUpstreamHandler();
        handler.MapJson("https://api.nuget.org/v3/index.json", TestFixtures.NuGetOrgServiceIndex);

        var httpClient = new TestHttpClientFactory(handler);
        IOptions<MirrorOptions> optionsWrapper = Microsoft.Extensions.Options.Options.Create(new MirrorOptions
        {
            Upstream = new UpstreamOptions
            {
                BackgroundRefresh = true,
                DiscoveryCacheTtl = TimeSpan.FromSeconds(1),
                DiscoveryRefreshInterval = TimeSpan.FromMilliseconds(100),
            },
        });

        var client = new UpstreamClient(httpClient, optionsWrapper, NullLogger<UpstreamClient>.Instance, new MirrorMetrics());
        var cache = new DiscoveryCache(client, optionsWrapper, NullLogger<DiscoveryCache>.Instance, new MirrorMetrics(), TimeProvider.System);
        var service = new DiscoveryRefreshService(cache, optionsWrapper, NullLogger<DiscoveryRefreshService>.Instance);

        using var cts = new CancellationTokenSource();
        MethodInfo executeAsync = typeof(DiscoveryRefreshService)
            .GetMethod("ExecuteAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?? throw new InvalidOperationException("DiscoveryRefreshService.ExecuteAsync method not found.");

        var task = (Task)(executeAsync.Invoke(service, [cts.Token])
            ?? throw new InvalidOperationException("Invoke returned null."));

        await Task.Delay(500, TestContext.Current.CancellationToken);
        await cts.CancelAsync();

        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }

        Assert.True(handler.GetCount("https://api.nuget.org/v3/index.json") >= 2,
            "Expected at least warm-up refresh and one periodic refresh.");
    }

    [Fact]
    public async Task Service_ShortCircuits_WhenBackgroundRefreshDisabled()
    {
        var handler = new StubUpstreamHandler();
        handler.MapJson("https://api.nuget.org/v3/index.json", TestFixtures.NuGetOrgServiceIndex);

        var httpClient = new TestHttpClientFactory(handler);
        IOptions<MirrorOptions> optionsWrapper = Microsoft.Extensions.Options.Options.Create(new MirrorOptions
        {
            Upstream = new UpstreamOptions
            {
                BackgroundRefresh = false,
            },
        });

        var client = new UpstreamClient(httpClient, optionsWrapper, NullLogger<UpstreamClient>.Instance, new MirrorMetrics());
        var cache = new DiscoveryCache(client, optionsWrapper, NullLogger<DiscoveryCache>.Instance, new MirrorMetrics(), TimeProvider.System);
        var service = new DiscoveryRefreshService(cache, optionsWrapper, NullLogger<DiscoveryRefreshService>.Instance);

        MethodInfo executeAsync = typeof(DiscoveryRefreshService)
            .GetMethod("ExecuteAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?? throw new InvalidOperationException("DiscoveryRefreshService.ExecuteAsync method not found.");

        var task = (Task)(executeAsync.Invoke(service, [CancellationToken.None])
            ?? throw new InvalidOperationException("Invoke returned null."));

        await task;

        Assert.Equal(0, handler.GetCount("https://api.nuget.org/v3/index.json"));
    }
}
