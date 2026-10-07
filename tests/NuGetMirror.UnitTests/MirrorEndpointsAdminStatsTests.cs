using System.Reflection;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NuGetMirror.Configuration;
using NuGetMirror.Diagnostics;
using NuGetMirror.Discovery;
using NuGetMirror.Proxy;
using NuGetMirror.Storage;
using NuGetMirror.TestKit;
using NuGetMirror.Upstream;

namespace NuGetMirror.UnitTests;

public sealed class MirrorEndpointsAdminStatsTests
{
    private const string UpstreamIndexUrl = "https://api.nuget.org/v3/index.json";

    // ── Discovery snapshot available ────────────────────────────────────────

    [Fact]
    public async Task AdminStats_SnapshotAvailable_ReturnsFullResponse()
    {
        DiscoveryCache discovery = CreateDiscoveryWithSnapshot(resourceCount: 5);
        var cacheStats = new CacheStatsState(TimeProvider.System);
        cacheStats.Update(bytes: 1024, entries: 3);
        var store = new HealthyProbeStore();

        string body = await InvokeAdminStats(discovery, CreateOptions(), cacheStats, store);
        using var doc = JsonDocument.Parse(body);

        Assert.True(doc.RootElement.TryGetProperty("upstream", out JsonElement upstream));
        Assert.Equal(UpstreamIndexUrl, upstream.GetProperty("indexUrl").GetString());
        Assert.NotNull(upstream.GetProperty("discoveredAt").GetString());
        Assert.Equal(5, upstream.GetProperty("resourceCount").GetInt32());

        Assert.True(doc.RootElement.TryGetProperty("cache", out JsonElement cache));
        Assert.True(cache.GetProperty("enabled").GetBoolean());
        Assert.True(cache.GetProperty("evictionEnabled").GetBoolean());
        Assert.Equal(1024, cache.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(3, cache.GetProperty("entryCount").GetInt64());
        Assert.NotNull(cache.GetProperty("lastUpdatedUtc").GetString());

        Assert.True(doc.RootElement.TryGetProperty("storage", out JsonElement storage));
        Assert.True(storage.GetProperty("healthy").GetBoolean());
        Assert.Null(storage.GetProperty("error").GetString());
    }

    // ── Discovery snapshot unavailable ──────────────────────────────────────

    [Fact]
    public async Task AdminStats_SnapshotUnavailable_ReturnsNullDiscoveryData()
    {
        DiscoveryCache discovery = CreateDiscoveryWithoutSnapshot();
        var cacheStats = new CacheStatsState(TimeProvider.System);

        string body = await InvokeAdminStats(discovery, CreateOptions(), cacheStats, store: null);
        using var doc = JsonDocument.Parse(body);

        Assert.True(doc.RootElement.TryGetProperty("upstream", out JsonElement upstream));
        Assert.Equal(UpstreamIndexUrl, upstream.GetProperty("indexUrl").GetString());
        Assert.Null(upstream.GetProperty("discoveredAt").GetString());
        Assert.Equal(0, upstream.GetProperty("resourceCount").GetInt32());
    }

    // ── Storage health probe ────────────────────────────────────────────────

    [Fact]
    public async Task AdminStats_StorageProbeHealthy_ReturnsHealthyTrue()
    {
        var store = new HealthyProbeStore();
        string body = await InvokeAdminStats(CreateDiscoveryWithSnapshot(), CreateOptions(), new CacheStatsState(TimeProvider.System), store);
        using var doc = JsonDocument.Parse(body);

        Assert.True(doc.RootElement.GetProperty("storage").GetProperty("healthy").GetBoolean());
    }

    [Fact]
    public async Task AdminStats_StorageProbeUnhealthy_ReturnsHealthyFalseWithError()
    {
        var store = new UnhealthyProbeStore("Disk full");
        string body = await InvokeAdminStats(CreateDiscoveryWithSnapshot(), CreateOptions(), new CacheStatsState(TimeProvider.System), store);
        using var doc = JsonDocument.Parse(body);

        Assert.False(doc.RootElement.GetProperty("storage").GetProperty("healthy").GetBoolean());
        Assert.Equal("Disk full", doc.RootElement.GetProperty("storage").GetProperty("error").GetString());
    }

    [Fact]
    public async Task AdminStats_NoStore_StorageHealthyIsNull()
    {
        string body = await InvokeAdminStats(CreateDiscoveryWithSnapshot(), CreateOptions(), new CacheStatsState(TimeProvider.System), store: null);
        using var doc = JsonDocument.Parse(body);

        JsonElement healthy = doc.RootElement.GetProperty("storage").GetProperty("healthy");
        Assert.Equal(JsonValueKind.Null, healthy.ValueKind);
    }

    [Fact]
    public async Task AdminStats_StoreWithoutProbe_StorageHealthyIsTrue()
    {
        var store = new NoProbeStore();
        string body = await InvokeAdminStats(CreateDiscoveryWithSnapshot(), CreateOptions(), new CacheStatsState(TimeProvider.System), store);
        using var doc = JsonDocument.Parse(body);

        Assert.True(doc.RootElement.GetProperty("storage").GetProperty("healthy").GetBoolean());
    }

    // ── Cache stats state ───────────────────────────────────────────────────

    [Fact]
    public async Task AdminStats_CacheStatsNotUpdated_LastUpdatedUtcIsNull()
    {
        var cacheStats = new CacheStatsState(TimeProvider.System); // default — LastUpdateUtc == MinValue

        string body = await InvokeAdminStats(CreateDiscoveryWithSnapshot(), CreateOptions(), cacheStats, store: null);
        using var doc = JsonDocument.Parse(body);

        JsonElement lastUpdated = doc.RootElement.GetProperty("cache").GetProperty("lastUpdatedUtc");
        Assert.Equal(JsonValueKind.Null, lastUpdated.ValueKind);
    }

    [Fact]
    public async Task AdminStats_CacheStatsUpdated_IncludesSizeAndCount()
    {
        var cacheStats = new CacheStatsState(TimeProvider.System);
        cacheStats.Update(bytes: 4096, entries: 7);

        string body = await InvokeAdminStats(CreateDiscoveryWithSnapshot(), CreateOptions(), cacheStats, store: null);
        using var doc = JsonDocument.Parse(body);

        JsonElement cache = doc.RootElement.GetProperty("cache");
        Assert.Equal(4096, cache.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(7, cache.GetProperty("entryCount").GetInt64());
        Assert.NotNull(cache.GetProperty("lastUpdatedUtc").GetString());
    }

    [Fact]
    public async Task AdminStats_CacheDefaults_IncludesNegativeCacheFields()
    {
        MirrorOptions options = CreateOptions();
        options.Cache.NegativeCache.Enabled = true;

        string body = await InvokeAdminStats(CreateDiscoveryWithSnapshot(), options, new CacheStatsState(TimeProvider.System), store: null);
        using var doc = JsonDocument.Parse(body);

        JsonElement cache = doc.RootElement.GetProperty("cache");
        Assert.True(cache.GetProperty("negativeCacheEnabled").GetBoolean());
        Assert.Equal(0, cache.GetProperty("negativeCacheEntries").GetInt32());
    }

    // ── Response shape ──────────────────────────────────────────────────────

    [Fact]
    public async Task AdminStats_Response_ContainsVersionAndUptime()
    {
        string body = await InvokeAdminStats(CreateDiscoveryWithSnapshot(), CreateOptions(), new CacheStatsState(TimeProvider.System), store: null);
        using var doc = JsonDocument.Parse(body);

        Assert.True(doc.RootElement.TryGetProperty("version", out JsonElement version));
        Assert.NotNull(version.GetString());

        Assert.True(doc.RootElement.TryGetProperty("uptime", out JsonElement uptime));
        Assert.NotNull(uptime.GetString());
    }

    [Fact]
    public async Task AdminStats_Response_ContentTypeIsApplicationJson()
    {
        (string _, DefaultHttpContext? context) = await InvokeAdminStatsRaw(
            CreateDiscoveryWithSnapshot(), CreateOptions(), new CacheStatsState(TimeProvider.System), store: null);

        Assert.Equal("application/json", context.Response.ContentType);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static MirrorOptions CreateOptions()
    {
        return new MirrorOptions
        {
            Upstream = { IndexUrl = UpstreamIndexUrl },
            Cache = { Enabled = true, Eviction = { Enabled = true } },
        };
    }

    private static DiscoveryCache CreateDiscoveryWithSnapshot(int resourceCount = 3)
    {
        IOptions<MirrorOptions> options = Options.Create(new MirrorOptions { Upstream = { IndexUrl = UpstreamIndexUrl } });
        var handler = new StubUpstreamHandler();
        var httpClientFactory = new TestHttpClientFactory(handler);
        var upstreamClient = new UpstreamClient(httpClientFactory, options, NullLogger<UpstreamClient>.Instance, new MirrorMetrics());
        var discovery = new DiscoveryCache(upstreamClient, options, NullLogger<DiscoveryCache>.Instance, new MirrorMetrics(), TimeProvider.System);

        var forwardMap = new Dictionary<string, string>();
        for (int i = 0; i < resourceCount; i++)
        {
            forwardMap[$"/resource-{i}/"] = $"https://upstream.example/resource-{i}/";
        }

        var snapshot = new DiscoverySnapshot(DateTimeOffset.UtcNow, "{}", forwardMap, []);
        FieldInfo field = typeof(DiscoveryCache).GetField("_current", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("DiscoveryCache._current field not found.");
        field.SetValue(discovery, snapshot);
        return discovery;
    }

    private static DiscoveryCache CreateDiscoveryWithoutSnapshot()
    {
        IOptions<MirrorOptions> options = Options.Create(new MirrorOptions { Upstream = { IndexUrl = UpstreamIndexUrl } });
        var handler = new StubUpstreamHandler();
        var httpClientFactory = new TestHttpClientFactory(handler);
        var upstreamClient = new UpstreamClient(httpClientFactory, options, NullLogger<UpstreamClient>.Instance, new MirrorMetrics());
        // Not seeded — CurrentSnapshot will throw InvalidOperationException.
        return new DiscoveryCache(upstreamClient, options, NullLogger<DiscoveryCache>.Instance, new MirrorMetrics(), TimeProvider.System);
    }

    private static async Task<string> InvokeAdminStats(
        DiscoveryCache discovery, MirrorOptions options, CacheStatsState cacheStats, IPackageContentStore? store)
    {
        (string _, DefaultHttpContext? context) = await InvokeAdminStatsRaw(discovery, options, cacheStats, store);
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8, leaveOpen: true);
        return await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<(string Body, DefaultHttpContext Context)> InvokeAdminStatsRaw(
        DiscoveryCache discovery, MirrorOptions options, CacheStatsState cacheStats, IPackageContentStore? store)
    {
        IOptions<MirrorOptions> optionsWrapper = Options.Create(options);
        MethodInfo method = typeof(MirrorEndpoints).GetMethod(
            "AdminStatsHandlerAsync",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("AdminStatsHandlerAsync method not found.");

        var negativeCache = new NegativeCache(options.Cache.NegativeCache.MaxEntries, TimeProvider.System);
        var task = (Task<IResult>)method.Invoke(null, [discovery, optionsWrapper, cacheStats, store, NullLoggerFactory.Instance, TimeProvider.System, negativeCache])!;
        IResult result = await task;

        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();

        await result.ExecuteAsync(context);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8, leaveOpen: true);
        string body = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
        return (body, context);
    }

    // ── Inner store types ────────────────────────────────────────────────────

    private sealed class HealthyProbeStore : IPackageContentStore, IStorageHealthProbe
    {
        public bool CheckCalled { get; private set; }

        public ValueTask CheckAsync(CancellationToken ct)
        {
            CheckCalled = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask<CachedContent?> TryGetAsync(string key, CancellationToken ct)
            => ValueTask.FromResult<CachedContent?>(null);

        public ValueTask<ICacheWriteHandle> BeginWriteAsync(string key, CancellationToken ct)
            => throw new NotSupportedException();
    }

    private sealed class UnhealthyProbeStore(string errorMessage) : IPackageContentStore, IStorageHealthProbe
    {
        public ValueTask CheckAsync(CancellationToken ct)
            => throw new InvalidOperationException(errorMessage);

        public ValueTask<CachedContent?> TryGetAsync(string key, CancellationToken ct)
            => ValueTask.FromResult<CachedContent?>(null);

        public ValueTask<ICacheWriteHandle> BeginWriteAsync(string key, CancellationToken ct)
            => throw new NotSupportedException();
    }

    private sealed class NoProbeStore : IPackageContentStore
    {
        public ValueTask<CachedContent?> TryGetAsync(string key, CancellationToken ct)
            => ValueTask.FromResult<CachedContent?>(null);

        public ValueTask<ICacheWriteHandle> BeginWriteAsync(string key, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
