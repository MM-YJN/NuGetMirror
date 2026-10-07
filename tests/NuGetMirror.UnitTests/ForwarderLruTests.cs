using System.Net;
using System.Net.Http.Headers;
using System.Reflection;

using Microsoft.AspNetCore.Http;
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

public sealed class ForwarderLruTests
{
    private const string RoutePrefix = "/v3-flatcontainer/";
    private const string UpstreamBase = "http://upstream.example/v3-flatcontainer/";
    private const string RemainingPath = "test.pkg/1.0.0/test.pkg.1.0.0.nupkg";
    private static readonly byte[] s_bodyBytes = "fake-nupkg"u8.ToArray();

    // ── Registration LRU touch ─────────────────────────────────────────────

    private const string RegRoutePrefix = "/v3/registration-semver2/";
    private const string RegFlavor = "semver2";
    private const string RegPath = "newtonsoft.json/index.json";
    private const string RegCacheKey = "$registration/semver2/newtonsoft.json/index.json";

    [Fact]
    public async Task GetCacheHit_TouchesFile_WhenLruEnabled()
    {
        var bodyStream = new MemoryStream();
        DefaultHttpContext context = CreateHttpContext(bodyStream);
        var store = new LruRecordingStore(returnsCacheHit: true);
        Forwarder forwarder = CreateForwarder(store, EvictionStrategy.Lru, evictionEnabled: true);

        await forwarder.StreamProxyAsync(context, RoutePrefix, RemainingPath);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.True(store.TouchCalled, "TouchAsync should be called on LRU cache hit.");
        Assert.Single(store.TouchedKeys);
        Assert.Equal("test.pkg/1.0.0/test.pkg.1.0.0.nupkg", store.TouchedKeys[0]);
    }

    [Fact]
    public async Task GetCacheHit_DoesNotTouch_WhenOldestStrategy()
    {
        var bodyStream = new MemoryStream();
        DefaultHttpContext context = CreateHttpContext(bodyStream);
        var store = new LruRecordingStore(returnsCacheHit: true);
        Forwarder forwarder = CreateForwarder(store, EvictionStrategy.Oldest, evictionEnabled: true);

        await forwarder.StreamProxyAsync(context, RoutePrefix, RemainingPath);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.False(store.TouchCalled, "TouchAsync should not be called with Oldest strategy.");
    }

    [Fact]
    public async Task GetCacheHit_DoesNotTouch_WhenEvictionDisabled()
    {
        var bodyStream = new MemoryStream();
        DefaultHttpContext context = CreateHttpContext(bodyStream);
        var store = new LruRecordingStore(returnsCacheHit: true);
        Forwarder forwarder = CreateForwarder(store, EvictionStrategy.Lru, evictionEnabled: false);

        await forwarder.StreamProxyAsync(context, RoutePrefix, RemainingPath);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.False(store.TouchCalled, "TouchAsync should not be called when eviction is disabled.");
    }

    [Fact]
    public async Task GetCacheHit_DoesNotTouch_WhenStoreNotICacheMaintenance()
    {
        var bodyStream = new MemoryStream();
        DefaultHttpContext context = CreateHttpContext(bodyStream);
        var store = new StoreWithoutMaintenance(returnsCacheHit: true);
        Forwarder forwarder = CreateForwarder(store, EvictionStrategy.Lru, evictionEnabled: true);

        await forwarder.StreamProxyAsync(context, RoutePrefix, RemainingPath);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task HeadCacheHit_TouchesFile_WhenLruEnabled()
    {
        var bodyStream = new MemoryStream();
        DefaultHttpContext context = CreateHttpHeadContext(bodyStream);
        var store = new LruRecordingStore(returnsCacheHit: true);
        Forwarder forwarder = CreateForwarder(store, EvictionStrategy.Lru, evictionEnabled: true);

        await forwarder.StreamProxyAsync(context, RoutePrefix, RemainingPath);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.True(store.TouchCalled, "TouchAsync should be called on LRU HEAD cache hit.");
    }

    [Fact]
    public async Task GetRegistrationCacheHit_TouchesFile_WhenLruEnabled()
    {
        var bodyStream = new MemoryStream();
        DefaultHttpContext context = CreateHttpContext(bodyStream);
        var store = new LruRecordingStore(returnsCacheHit: true);
        Forwarder forwarder = CreateRegistrationForwarder(store, EvictionStrategy.Lru, evictionEnabled: true);

        await forwarder.RegistrationProxyAsync(context, RegRoutePrefix, RegFlavor, RegPath);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.True(store.TouchCalled, "TouchAsync should be called on LRU registration cache hit.");
        Assert.Single(store.TouchedKeys);
        Assert.Equal(RegCacheKey, store.TouchedKeys[0]);
    }

    [Fact]
    public async Task GetRegistrationCacheHit_DoesNotTouch_WhenOldestStrategy()
    {
        var bodyStream = new MemoryStream();
        DefaultHttpContext context = CreateHttpContext(bodyStream);
        var store = new LruRecordingStore(returnsCacheHit: true);
        Forwarder forwarder = CreateRegistrationForwarder(store, EvictionStrategy.Oldest, evictionEnabled: true);

        await forwarder.RegistrationProxyAsync(context, RegRoutePrefix, RegFlavor, RegPath);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.False(store.TouchCalled, "TouchAsync should not be called on registration cache hit with Oldest strategy.");
    }

    [Fact]
    public async Task GetRegistrationCacheHit_DoesNotTouch_WhenEvictionDisabled()
    {
        var bodyStream = new MemoryStream();
        DefaultHttpContext context = CreateHttpContext(bodyStream);
        var store = new LruRecordingStore(returnsCacheHit: true);
        Forwarder forwarder = CreateRegistrationForwarder(store, EvictionStrategy.Lru, evictionEnabled: false);

        await forwarder.RegistrationProxyAsync(context, RegRoutePrefix, RegFlavor, RegPath);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.False(store.TouchCalled, "TouchAsync should not be called when eviction is disabled.");
    }

    [Fact]
    public async Task GetRegistrationCacheHit_DoesNotTouch_WhenStoreNotICacheMaintenance()
    {
        var bodyStream = new MemoryStream();
        DefaultHttpContext context = CreateHttpContext(bodyStream);
        var store = new StoreWithoutMaintenance(returnsCacheHit: true);
        Forwarder forwarder = CreateRegistrationForwarder(store, EvictionStrategy.Lru, evictionEnabled: true);

        await forwarder.RegistrationProxyAsync(context, RegRoutePrefix, RegFlavor, RegPath);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    private static DefaultHttpContext CreateHttpContext(MemoryStream responseBody)
    {
        var context = new DefaultHttpContext();

        context.Request.Method = HttpMethods.Get;
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("localhost:5049");
        context.Response.Body = responseBody;

        return context;
    }

    private static DefaultHttpContext CreateHttpHeadContext(MemoryStream responseBody)
    {
        var context = new DefaultHttpContext();

        context.Request.Method = HttpMethods.Head;
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("localhost:5049");
        context.Response.Body = responseBody;

        return context;
    }

    private static Forwarder CreateForwarder(IPackageContentStore store, EvictionStrategy strategy, bool evictionEnabled)
    {
        IOptions<MirrorOptions> optionsWrapper = Microsoft.Extensions.Options.Options.Create(new MirrorOptions
        {
            Cache = new CacheOptions
            {
                Enabled = true,
                Backend = "FileSystem",
                FileSystem = new FileSystemOptions { Directory = "/tmp" },
                Eviction = new CacheEvictionOptions
                {
                    Enabled = evictionEnabled,
                    Strategy = strategy,
                },
            },
        });

        var handler = new StubUpstreamHandler();
        string upstreamUrl = UpstreamBase + RemainingPath;

        handler.MapFactory(upstreamUrl, () =>
        {
            var content = new ByteArrayContent(s_bodyBytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
            };
        });

        var httpClient = new TestHttpClientFactory(handler);
        var upstreamClient = new UpstreamClient(httpClient, optionsWrapper, NullLogger<UpstreamClient>.Instance, new MirrorMetrics());

        var cache = new DiscoveryCache(upstreamClient, optionsWrapper, NullLogger<DiscoveryCache>.Instance, new MirrorMetrics(), TimeProvider.System);
        SeedSnapshot(cache);

        var keyedLock = new KeyedAsyncLock(NullLogger<KeyedAsyncLock>.Instance);

        return new Forwarder(upstreamClient, cache, optionsWrapper, NullLogger<Forwarder>.Instance, new MirrorMetrics(), store, keyedLock);
    }

    private static void SeedSnapshot(DiscoveryCache cache)
    {
        var snapshot = new DiscoverySnapshot(
            DateTimeOffset.UtcNow,
            "{}",
            new Dictionary<string, string> { { RoutePrefix, UpstreamBase } },
            []);

        FieldInfo field = typeof(DiscoveryCache).GetField(
            "_current",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("DiscoveryCache._current field not found.");

        field.SetValue(cache, snapshot);
    }

    private static Forwarder CreateRegistrationForwarder(IPackageContentStore store, EvictionStrategy strategy, bool evictionEnabled)
    {
        IOptions<MirrorOptions> optionsWrapper = Microsoft.Extensions.Options.Options.Create(new MirrorOptions
        {
            Cache = new CacheOptions
            {
                Enabled = true,
                Backend = "FileSystem",
                FileSystem = new FileSystemOptions { Directory = "/tmp" },
                Registration = new RegistrationCacheOptions { Enabled = true },
                Eviction = new CacheEvictionOptions
                {
                    Enabled = evictionEnabled,
                    Strategy = strategy,
                },
            },
        });

        var handler = new StubUpstreamHandler();
        var httpClient = new TestHttpClientFactory(handler);
        var upstreamClient = new UpstreamClient(httpClient, optionsWrapper, NullLogger<UpstreamClient>.Instance, new MirrorMetrics());

        var cache = new DiscoveryCache(upstreamClient, optionsWrapper, NullLogger<DiscoveryCache>.Instance, new MirrorMetrics(), TimeProvider.System);
        SeedRegistrationSnapshot(cache);

        var keyedLock = new KeyedAsyncLock(NullLogger<KeyedAsyncLock>.Instance);

        return new Forwarder(upstreamClient, cache, optionsWrapper, NullLogger<Forwarder>.Instance, new MirrorMetrics(), store, keyedLock);
    }

    private static void SeedRegistrationSnapshot(DiscoveryCache cache)
    {
        var snapshot = new DiscoverySnapshot(
            DateTimeOffset.UtcNow,
            "{}",
            new Dictionary<string, string> { { RegRoutePrefix, "https://api.nuget.org/v3/registration5-gz-semver2/" } },
            [new RewritePair("https://api.nuget.org/v3/registration5-gz-semver2/", RegRoutePrefix),
             new RewritePair("https://api.nuget.org/v3-flatcontainer/", "/v3-flatcontainer/")]);

        FieldInfo field = typeof(DiscoveryCache).GetField(
            "_current",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("DiscoveryCache._current field not found.");

        field.SetValue(cache, snapshot);
    }

    private sealed class LruRecordingStore(bool returnsCacheHit, byte[]? cachedBody = null, string contentType = "application/octet-stream") : IPackageContentStore, ICacheMaintenance
    {
        private readonly byte[] _cachedBody = cachedBody ?? "cached-content"u8.ToArray();

        public bool TouchCalled { get; private set; }

        public List<string> TouchedKeys { get; } = [];

        public List<string> EnumeratedKeys { get; } = [];

        public List<string> DeletedKeys { get; } = [];

        public ValueTask<CachedContent?> TryGetAsync(string key, CancellationToken ct)
        {
            if (returnsCacheHit)
            {
                var stream = new MemoryStream(_cachedBody);
                return ValueTask.FromResult<CachedContent?>(new CachedContent
                {
                    Stream = stream,
                    Length = _cachedBody.Length,
                    ContentType = contentType,
                    StoredAtUtc = DateTimeOffset.UtcNow,
                });
            }

            return ValueTask.FromResult<CachedContent?>(null);
        }

        public ValueTask<ICacheWriteHandle> BeginWriteAsync(string key, CancellationToken ct)
            => throw new NotSupportedException();

        public async IAsyncEnumerable<CacheEntryInfo> EnumerateAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            foreach (string key in EnumeratedKeys)
            {
                yield return new CacheEntryInfo(key, 100, DateTimeOffset.UtcNow);
            }

            await Task.CompletedTask;
        }

        public ValueTask DeleteAsync(string key, CancellationToken ct)
        {
            DeletedKeys.Add(key);
            return ValueTask.CompletedTask;
        }

        public ValueTask TouchAsync(string key, CancellationToken ct)
        {
            TouchCalled = true;
            TouchedKeys.Add(key);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StoreWithoutMaintenance(bool returnsCacheHit) : IPackageContentStore
    {
        public ValueTask<CachedContent?> TryGetAsync(string key, CancellationToken ct)
        {
            if (returnsCacheHit)
            {
                var stream = new MemoryStream("cached-content"u8.ToArray());
                return ValueTask.FromResult<CachedContent?>(new CachedContent { Stream = stream, Length = 14, ContentType = "application/octet-stream" });
            }

            return ValueTask.FromResult<CachedContent?>(null);
        }

        public ValueTask<ICacheWriteHandle> BeginWriteAsync(string key, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
