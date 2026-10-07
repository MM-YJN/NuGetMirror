using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;

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

public sealed class ForwarderReadmeProxyTests
{
    private const string RoutePrefix = "/v3-flatcontainer/";
    private const string UpstreamBase = "http://upstream.example/v3-flatcontainer/";
    private const string RemainingPath = "newtonsoft.json/13.0.3/readme";
    private const string CacheKey = "$readme/newtonsoft.json/13.0.3/readme";
    private static readonly byte[] s_readmeBytes = "# Newtonsoft.Json"u8.ToArray();

    // ── Cache disabled → live proxy ─────────────────────────────────────────

    [Fact]
    public async Task ReadmeProxy_CacheDisabled_FallsBackToLiveProxy()
    {
        var handler = new StubUpstreamHandler();
        handler.MapBytes(UpstreamBase + RemainingPath, s_readmeBytes, "text/markdown");
        var options = new MirrorOptions { Cache = { Enabled = false, Readme = { Enabled = true } } };
        Forwarder forwarder = CreateForwarder(options, handler, new NoOpStore());

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        await forwarder.ReadmeProxyAsync(context, RoutePrefix, RemainingPath);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(s_readmeBytes, ReadBody(context));
    }

    [Fact]
    public async Task ReadmeProxy_ReadmeCacheDisabled_FallsBackToLiveProxy()
    {
        var handler = new StubUpstreamHandler();
        handler.MapBytes(UpstreamBase + RemainingPath, s_readmeBytes, "text/markdown");
        var options = new MirrorOptions { Cache = { Enabled = true, Readme = { Enabled = false } } };
        Forwarder forwarder = CreateForwarder(options, handler, new NoOpStore());

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        await forwarder.ReadmeProxyAsync(context, RoutePrefix, RemainingPath);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(s_readmeBytes, ReadBody(context));
    }

    // ── Cache hit ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ReadmeProxy_CacheHit_ServesFromCacheAsTextMarkdown()
    {
        byte[] cachedBytes = "# Cached Readme"u8.ToArray();
        var store = new FixedContentStore(new CachedContent
        {
            Stream = new MemoryStream(cachedBytes),
            Length = cachedBytes.Length,
            ContentType = "text/markdown",
            StoredAtUtc = DateTimeOffset.UtcNow,
        });
        MirrorOptions options = CreateCacheEnabledOptions();
        Forwarder forwarder = CreateForwarder(options, new StubUpstreamHandler(), store);

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        await forwarder.ReadmeProxyAsync(context, RoutePrefix, RemainingPath);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("text/markdown", context.Response.ContentType);
        Assert.Equal(cachedBytes, ReadBody(context));
    }

    [Fact]
    public async Task ReadmeProxy_CacheHit_HeadRequest_ReturnsHeadersOnly()
    {
        byte[] cachedBytes = "# Cached Readme"u8.ToArray();
        var store = new FixedContentStore(new CachedContent
        {
            Stream = new MemoryStream(cachedBytes),
            Length = cachedBytes.Length,
            ContentType = "text/markdown",
            StoredAtUtc = DateTimeOffset.UtcNow,
        });
        MirrorOptions options = CreateCacheEnabledOptions();
        Forwarder forwarder = CreateForwarder(options, new StubUpstreamHandler(), store);

        DefaultHttpContext context = TestContextFactory.MakeHeadContext();
        await forwarder.ReadmeProxyAsync(context, RoutePrefix, RemainingPath);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("text/markdown", context.Response.ContentType);
        Assert.Equal(cachedBytes.Length, context.Response.ContentLength);
        Assert.Empty(ReadBody(context));
    }

    // ── Cache miss → upstream fetch ─────────────────────────────────────────

    [Fact]
    public async Task ReadmeProxy_CacheMiss_FetchesFromUpstream()
    {
        var handler = new StubUpstreamHandler();
        handler.MapBytes(UpstreamBase + RemainingPath, s_readmeBytes, "text/markdown");
        var store = new RecordingStore();
        MirrorOptions options = CreateCacheEnabledOptions();
        Forwarder forwarder = CreateForwarder(options, handler, store);

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        await forwarder.ReadmeProxyAsync(context, RoutePrefix, RemainingPath);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(s_readmeBytes, ReadBody(context));
        Assert.True(store.WasWritten, "Readme should be cached after upstream fetch.");
        Assert.Equal(CacheKey, store.LastWrittenKey);
    }

    // ── Upstream error → stale fallback ─────────────────────────────────────

    [Fact]
    public async Task ReadmeProxy_UpstreamError_WithStaleCache_ServesStaleFallback()
    {
        DateTimeOffset staleTime = DateTimeOffset.UtcNow - TimeSpan.FromHours(2);
        byte[] cachedBytes = "# Stale Readme"u8.ToArray();
        var store = new FixedContentStore(new CachedContent
        {
            Stream = new MemoryStream(cachedBytes),
            Length = cachedBytes.Length,
            ContentType = "text/markdown",
            StoredAtUtc = staleTime,
        });
        var handler = new StubUpstreamHandler();
        handler.MapStatus(UpstreamBase + RemainingPath, HttpStatusCode.InternalServerError);
        MirrorOptions options = CreateCacheEnabledOptions();
        Forwarder forwarder = CreateForwarder(options, handler, store);

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        await forwarder.ReadmeProxyAsync(context, RoutePrefix, RemainingPath);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(cachedBytes, ReadBody(context));
    }

    // ── ETag revalidation (304) ─────────────────────────────────────────────

    [Fact]
    public async Task ReadmeProxy_ETagRevalidation_304_ServesFromCache()
    {
        var readmeOptions = new ReadmeOptions();
        DateTimeOffset staleTime = DateTimeOffset.UtcNow - readmeOptions.CacheTtl - TimeSpan.FromSeconds(10);
        byte[] cachedBytes = "# Cached With ETag"u8.ToArray();
        var store = new FixedContentStore(new CachedContent
        {
            Stream = new MemoryStream(cachedBytes),
            Length = cachedBytes.Length,
            ContentType = "text/markdown",
            ETag = "\"etag123\"",
            StoredAtUtc = staleTime,
        });
        var handler = new StubUpstreamHandler();
        handler.MapFactory(UpstreamBase + RemainingPath, () => new HttpResponseMessage(HttpStatusCode.NotModified));
        MirrorOptions options = CreateCacheEnabledOptions();
        Forwarder forwarder = CreateForwarder(options, handler, store);

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        await forwarder.ReadmeProxyAsync(context, RoutePrefix, RemainingPath);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(cachedBytes, ReadBody(context));
    }

    // ── LRU touch on hit ────────────────────────────────────────────────────

    [Fact]
    public async Task ReadmeProxy_CacheHit_LruEnabled_TouchesCacheEntry()
    {
        byte[] cachedBytes = "# Cached Readme"u8.ToArray();
        var store = new LruRecordingStore(cachedBytes);
        var options = new MirrorOptions
        {
            Cache = new CacheOptions
            {
                Enabled = true,
                Readme = { Enabled = true },
                Eviction = new CacheEvictionOptions { Enabled = true, Strategy = EvictionStrategy.Lru },
            },
        };
        Forwarder forwarder = CreateForwarder(options, new StubUpstreamHandler(), store);

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        await forwarder.ReadmeProxyAsync(context, RoutePrefix, RemainingPath);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.True(store.TouchCalled, "TouchAsync should be called on LRU readme cache hit.");
        Assert.Single(store.TouchedKeys);
        Assert.Equal(CacheKey, store.TouchedKeys[0]);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static MirrorOptions CreateCacheEnabledOptions()
    {
        return new MirrorOptions
        {
            Cache = { Enabled = true, Readme = { Enabled = true } },
        };
    }

    private static byte[] ReadBody(DefaultHttpContext context)
    {
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var ms = new MemoryStream();
        context.Response.Body.CopyTo(ms);
        return ms.ToArray();
    }

    private static Forwarder CreateForwarder(MirrorOptions options, HttpMessageHandler handler, IPackageContentStore store)
    {
        IOptions<MirrorOptions> optionsWrapper = Options.Create(options);
        var httpClientFactory = new TestHttpClientFactory(handler);
        var upstreamClient = new UpstreamClient(httpClientFactory, optionsWrapper, NullLogger<UpstreamClient>.Instance, new MirrorMetrics());
        var discovery = new DiscoveryCache(upstreamClient, optionsWrapper, NullLogger<DiscoveryCache>.Instance, new MirrorMetrics(), TimeProvider.System);
        SeedSnapshot(discovery);
        var keyedLock = new KeyedAsyncLock(NullLogger<KeyedAsyncLock>.Instance);
        return new Forwarder(upstreamClient, discovery, optionsWrapper, NullLogger<Forwarder>.Instance, new MirrorMetrics(), store, keyedLock);
    }

    private static void SeedSnapshot(DiscoveryCache cache)
    {
        var snapshot = new DiscoverySnapshot(
            DateTimeOffset.UtcNow,
            "{}",
            new Dictionary<string, string> { [RoutePrefix] = UpstreamBase },
            []);
        FieldInfo field = typeof(DiscoveryCache).GetField("_current", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("DiscoveryCache._current field not found.");
        field.SetValue(cache, snapshot);
    }

    // ── Inner store types ────────────────────────────────────────────────────

    private sealed class NoOpStore : IPackageContentStore
    {
        public ValueTask<CachedContent?> TryGetAsync(string key, CancellationToken ct)
            => ValueTask.FromResult<CachedContent?>(null);

        public ValueTask<ICacheWriteHandle> BeginWriteAsync(string key, CancellationToken ct)
            => throw new NotSupportedException();
    }

    private sealed class FixedContentStore(CachedContent content) : IPackageContentStore
    {
        public ValueTask<CachedContent?> TryGetAsync(string key, CancellationToken ct)
            => ValueTask.FromResult<CachedContent?>(content);

        public ValueTask<ICacheWriteHandle> BeginWriteAsync(string key, CancellationToken ct)
            => throw new NotSupportedException();
    }

    private sealed class RecordingStore : IPackageContentStore
    {
        public bool WasWritten { get; private set; }
        public string? LastWrittenKey { get; private set; }

        public ValueTask<CachedContent?> TryGetAsync(string key, CancellationToken ct)
            => ValueTask.FromResult<CachedContent?>(null);

        public ValueTask<ICacheWriteHandle> BeginWriteAsync(string key, CancellationToken ct)
        {
            LastWrittenKey = key;
            return ValueTask.FromResult<ICacheWriteHandle>(new RecordingWriteHandle(bytes => WasWritten = bytes > 0));
        }

        private sealed class RecordingWriteHandle(Action<int> onCommit) : ICacheWriteHandle
        {
            private readonly MemoryStream _stream = new();

            public Stream Stream => _stream;

            public void SetMetadata(string contentType, long length, string? etag) { }

            public ValueTask CommitAsync(CancellationToken ct)
            {
                onCommit((int)_stream.Length);
                return ValueTask.CompletedTask;
            }

            public async ValueTask DisposeAsync() => await _stream.DisposeAsync();
        }
    }

    private sealed class LruRecordingStore(byte[] cachedBytes) : IPackageContentStore, ICacheMaintenance
    {
        public bool TouchCalled { get; private set; }
        public List<string> TouchedKeys { get; } = [];

        public ValueTask<CachedContent?> TryGetAsync(string key, CancellationToken ct)
            => ValueTask.FromResult<CachedContent?>(new CachedContent
            {
                Stream = new MemoryStream(cachedBytes),
                Length = cachedBytes.Length,
                ContentType = "text/markdown",
                StoredAtUtc = DateTimeOffset.UtcNow,
            });

        public ValueTask<ICacheWriteHandle> BeginWriteAsync(string key, CancellationToken ct)
            => throw new NotSupportedException();

        public async IAsyncEnumerable<CacheEntryInfo> EnumerateAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DeleteAsync(string key, CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask TouchAsync(string key, CancellationToken ct)
        {
            TouchCalled = true;
            TouchedKeys.Add(key);
            return ValueTask.CompletedTask;
        }
    }
}
