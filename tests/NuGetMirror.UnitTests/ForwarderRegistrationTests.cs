using System.Net;
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

public sealed class ForwarderRegistrationTests
{
    private const string RoutePrefix = "/v3/registration-semver2/";
    private const string Flavor = "semver2";
    private const string UpstreamBase = "https://api.nuget.org/v3/registration5-gz-semver2/";
    private const string Path = "newtonsoft.json/index.json";
    private const string CacheKey = "$registration/semver2/newtonsoft.json/index.json";
    private const string RawUpstreamJson = """
    {
      "items": [
        {
          "packageContent": "https://api.nuget.org/v3-flatcontainer/newtonsoft.json/13.0.3/newtonsoft.json.13.0.3.nupkg"
        }
      ]
    }
    """;

    // ── Path traversal ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OversizedConcurrentResponses_StopAndNeverCache(bool caching)
    {
        var handler = new StubUpstreamHandler();
        GeneratedBodyStream[] streams = Enumerable.Range(0, 8).Select(_ => new GeneratedBodyStream(long.MaxValue)).ToArray();
        for (int i = 0; i < streams.Length; i++)
        {
            GeneratedBodyStream stream = streams[i];
            handler.MapFactory(UpstreamBase + $"package{i}/index.json", () =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
        }

        using StubUpstreamHandler upstreamHandler = handler;
        var options = new MirrorOptions
        {
            Cache = { Enabled = caching, Registration = { Enabled = true, MaxBodyBytes = 32 } },
            Upstream = { MaxRewriteBodyBytes = 32 },
        };
        var store = new RecordingStore(returnsCacheHit: false);
        Forwarder forwarder = CreateForwarderWithDiscovery(options, handler, store, new KeyedAsyncLock(NullLogger<KeyedAsyncLock>.Instance));
        await Task.WhenAll(streams.Select(async (stream, i) =>
        {
            DefaultHttpContext context = TestContextFactory.MakeGetContext();
            await forwarder.RegistrationProxyAsync(context, RoutePrefix, Flavor, $"package{i}/index.json");
            Assert.Equal(StatusCodes.Status502BadGateway, context.Response.StatusCode);
            Assert.Equal(33, stream.BytesRead);
            Assert.True(stream.Disposed);
        }));

        Assert.Null(store.LastWrittenKey);
    }

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("a/../b")]
    [InlineData(@"foo\\bar")]
    public async Task RegistrationProxy_PathTraversal_Returns404(string maliciousPath)
    {
        var options = new MirrorOptions
        {
            Cache = { Enabled = true, Registration = { Enabled = true } },
        };
        Forwarder forwarder = CreateForwarderWithCache(options, store: new NoOpStore());
        DefaultHttpContext context = TestContextFactory.MakeGetContext();

        await forwarder.RegistrationProxyAsync(context, RoutePrefix, Flavor, maliciousPath);

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
    }

    [Fact]
    public async Task RegistrationProxy_PathTraversal_Returns404_EvenWhenCachingDisabled()
    {
        var options = new MirrorOptions
        {
            Cache = { Enabled = false },
        };
        Forwarder forwarder = CreateForwarderWithCache(options, store: new NoOpStore());
        DefaultHttpContext context = TestContextFactory.MakeGetContext();

        await forwarder.RegistrationProxyAsync(context, RoutePrefix, Flavor, "../etc/passwd");

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
    }

    // ── Caching-disabled fallback ───────────────────────────────────────────

    [Fact]
    public async Task RegistrationProxy_CachingDisabled_FallsBackToLiveProxy()
    {
        var options = new MirrorOptions
        {
            Cache = { Enabled = false, Registration = { Enabled = true } },
            PublicBaseUrl = "http://localhost",
        };
        var upstreamHandler = new StubUpstreamHandler();
        upstreamHandler.MapJson(UpstreamBase + Path, RawUpstreamJson);

        Forwarder forwarder = CreateForwarderWithDiscovery(
            options, upstreamHandler, store: new NoOpStore(), keyedLock: null);
        DefaultHttpContext context = TestContextFactory.MakeGetContext();

        await forwarder.RegistrationProxyAsync(context, RoutePrefix, Flavor, Path);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        string body = ReadResponseBody(context);
        Assert.DoesNotContain("api.nuget.org", body, StringComparison.Ordinal);
        Assert.Contains("http://localhost/v3-flatcontainer/", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegistrationProxy_RegistrationCacheDisabled_FallsBackToLiveProxy()
    {
        var options = new MirrorOptions
        {
            Cache = { Enabled = true, Registration = { Enabled = false } },
            PublicBaseUrl = "http://localhost",
        };
        var upstreamHandler = new StubUpstreamHandler();
        upstreamHandler.MapJson(UpstreamBase + Path, RawUpstreamJson);

        Forwarder forwarder = CreateForwarderWithDiscovery(
            options, upstreamHandler, store: new NoOpStore(), keyedLock: null);
        DefaultHttpContext context = TestContextFactory.MakeGetContext();

        await forwarder.RegistrationProxyAsync(context, RoutePrefix, Flavor, Path);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        string body = ReadResponseBody(context);
        Assert.DoesNotContain("api.nuget.org", body, StringComparison.Ordinal);
    }

    // ── Cache hit ────────────────────────────────────────────────────────────

    [Fact]
    public async Task RegistrationProxy_CacheHit_ServesFromCacheWithRewriting()
    {
        byte[] bodyBytes = Encoding.UTF8.GetBytes(RawUpstreamJson);
        var store = new FixedContentStore(new CachedContent
        {
            Stream = new MemoryStream(bodyBytes),
            Length = bodyBytes.Length,
            ContentType = "application/json",
            StoredAtUtc = DateTimeOffset.UtcNow, // fresh
        });
        MirrorOptions options = CreateCacheEnabledOptions();
        Forwarder forwarder = CreateForwarderWithDiscovery(options, new StubUpstreamHandler(), store, new KeyedAsyncLock(NullLogger<KeyedAsyncLock>.Instance));
        DefaultHttpContext context = TestContextFactory.MakeGetContext();

        await forwarder.RegistrationProxyAsync(context, RoutePrefix, Flavor, Path);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        string body = ReadResponseBody(context);
        Assert.DoesNotContain("api.nuget.org", body, StringComparison.Ordinal);
        Assert.Contains("http://localhost/v3-flatcontainer/", body, StringComparison.Ordinal);
    }

    // ── Cache miss ───────────────────────────────────────────────────────────

    [Fact]
    public async Task RegistrationProxy_CacheMiss_FetchesFromUpstreamAndServes()
    {
        var upstreamHandler = new StubUpstreamHandler();
        upstreamHandler.MapJson(UpstreamBase + Path, RawUpstreamJson);

        var store = new RecordingStore(returnsCacheHit: false);
        MirrorOptions options = CreateCacheEnabledOptions();
        Forwarder forwarder = CreateForwarderWithDiscovery(options, upstreamHandler, store, new KeyedAsyncLock(NullLogger<KeyedAsyncLock>.Instance));
        DefaultHttpContext context = TestContextFactory.MakeGetContext();

        await forwarder.RegistrationProxyAsync(context, RoutePrefix, Flavor, Path);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(1, upstreamHandler.GetCount(UpstreamBase + Path));

        string body = ReadResponseBody(context);
        Assert.DoesNotContain("api.nuget.org", body, StringComparison.Ordinal);
        Assert.Contains("http://localhost/v3-flatcontainer/", body, StringComparison.Ordinal);

        Assert.True(store.WasWritten, "Response should be cached after fetch.");
        Assert.Equal(CacheKey, store.LastWrittenKey);
    }

    // ── ETag revalidation ────────────────────────────────────────────────────

    [Fact]
    public async Task RegistrationProxy_ETagRevalidation_ServesStaleFromCache()
    {
        byte[] bodyBytes = Encoding.UTF8.GetBytes(RawUpstreamJson);
        var registrationOptions = new RegistrationCacheOptions();
        DateTimeOffset staleTime = DateTimeOffset.UtcNow - registrationOptions.CacheTtl - TimeSpan.FromSeconds(10);

        var store = new FixedContentStore(new CachedContent
        {
            Stream = new MemoryStream(bodyBytes),
            Length = bodyBytes.Length,
            ContentType = "application/json",
            ETag = "\"some-etag\"",
            StoredAtUtc = staleTime, // stale — beyond TTL
        });

        var upstreamHandler = new StubUpstreamHandler();
        upstreamHandler.MapFactory(
            UpstreamBase + Path,
            () => new HttpResponseMessage(HttpStatusCode.NotModified));

        MirrorOptions options = CreateCacheEnabledOptions();
        Forwarder forwarder = CreateForwarderWithDiscovery(options, upstreamHandler, store, new KeyedAsyncLock(NullLogger<KeyedAsyncLock>.Instance));
        DefaultHttpContext context = TestContextFactory.MakeGetContext();

        await forwarder.RegistrationProxyAsync(context, RoutePrefix, Flavor, Path);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        string body = ReadResponseBody(context);
        Assert.DoesNotContain("api.nuget.org", body, StringComparison.Ordinal);
    }

    // ── Stale fallback ───────────────────────────────────────────────────────

    [Fact]
    public async Task RegistrationProxy_UpstreamError_WithPriorCache_ServesStaleFallback()
    {
        byte[] bodyBytes = Encoding.UTF8.GetBytes(RawUpstreamJson);
        var registrationOptions = new RegistrationCacheOptions();
        DateTimeOffset staleTime = DateTimeOffset.UtcNow - registrationOptions.CacheTtl - TimeSpan.FromSeconds(10);

        var store = new FixedContentStore(new CachedContent
        {
            Stream = new MemoryStream(bodyBytes),
            Length = bodyBytes.Length,
            ContentType = "application/json",
            StoredAtUtc = staleTime, // stale
        });

        var upstreamHandler = new StubUpstreamHandler();
        upstreamHandler.MapStatus(UpstreamBase + Path, HttpStatusCode.InternalServerError);

        MirrorOptions options = CreateCacheEnabledOptions();
        Forwarder forwarder = CreateForwarderWithDiscovery(options, upstreamHandler, store, new KeyedAsyncLock(NullLogger<KeyedAsyncLock>.Instance));
        DefaultHttpContext context = TestContextFactory.MakeGetContext();

        await forwarder.RegistrationProxyAsync(context, RoutePrefix, Flavor, Path);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        string body = ReadResponseBody(context);
        Assert.DoesNotContain("api.nuget.org", body, StringComparison.Ordinal);
    }

    // ── Null-snapshot passthrough ────────────────────────────────────────────

    [Fact]
    public async Task RegistrationProxy_NullSnapshot_ServesBodyUnrewritten()
    {
        byte[] bodyBytes = Encoding.UTF8.GetBytes(RawUpstreamJson);
        var store = new FixedContentStore(new CachedContent
        {
            Stream = new MemoryStream(bodyBytes),
            Length = bodyBytes.Length,
            ContentType = "application/json",
            StoredAtUtc = DateTimeOffset.UtcNow, // fresh
        });
        MirrorOptions options = CreateCacheEnabledOptions();
        Forwarder forwarder = CreateForwarderWithEmptyDiscovery(options, store);
        DefaultHttpContext context = TestContextFactory.MakeGetContext();

        await forwarder.RegistrationProxyAsync(context, RoutePrefix, Flavor, Path);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        string body = ReadResponseBody(context);
        Assert.Contains("api.nuget.org", body, StringComparison.Ordinal);
    }

    // ── Invalid cache entry length ──────────────────────────────────────────

    [Fact]
    public async Task RegistrationProxy_InvalidCacheLength_Returns500()
    {
        var store = new FixedContentStore(new CachedContent
        {
            Stream = new MemoryStream(),
            Length = -1,
            ContentType = "application/json",
            StoredAtUtc = DateTimeOffset.UtcNow, // fresh
        });
        MirrorOptions options = CreateCacheEnabledOptions();
        Forwarder forwarder = CreateForwarderWithDiscovery(
            options, new StubUpstreamHandler(), store, new KeyedAsyncLock(NullLogger<KeyedAsyncLock>.Instance));
        DefaultHttpContext context = TestContextFactory.MakeGetContext();

        await forwarder.RegistrationProxyAsync(context, RoutePrefix, Flavor, Path);

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
    }

    // ── Body-size limit ──────────────────────────────────────────────────────

    [Fact]
    public async Task RegistrationProxy_BodyExceedsMaxBodyBytes_Returns502()
    {
        string largeJson = new('x', 200);
        var upstreamHandler = new StubUpstreamHandler();
        upstreamHandler.MapJson(UpstreamBase + Path, largeJson);

        var options = new MirrorOptions
        {
            Cache = { Enabled = true, Registration = { Enabled = true, MaxBodyBytes = 10 } },
            PublicBaseUrl = "http://localhost",
        };
        var store = new NoOpStore();
        Forwarder forwarder = CreateForwarderWithDiscovery(options, upstreamHandler, store, new KeyedAsyncLock(NullLogger<KeyedAsyncLock>.Instance));
        DefaultHttpContext context = TestContextFactory.MakeGetContext();

        await forwarder.RegistrationProxyAsync(context, RoutePrefix, Flavor, Path);

        Assert.Equal(StatusCodes.Status502BadGateway, context.Response.StatusCode);
    }

    // ── Distinct cache keys per flavor ───────────────────────────────────────

    [Fact]
    public async Task RegistrationProxy_DifferentFlavorsUseDistinctCacheKeys()
    {
        var upstreamHandler = new StubUpstreamHandler();
        upstreamHandler.MapJson(
            "https://api.nuget.org/v3/registration5-gz-semver2/" + Path, RawUpstreamJson);
        upstreamHandler.MapJson(
            "https://api.nuget.org/v3/registration5-gz-semver1/" + Path, RawUpstreamJson);
        upstreamHandler.MapJson(
            "https://api.nuget.org/v3/registration5/" + Path, RawUpstreamJson);

        var store = new RecordingStore(returnsCacheHit: false);
        MirrorOptions options = CreateCacheEnabledOptions();
        Forwarder forwarder = CreateForwarder(options, upstreamHandler, new Dictionary<string, string>
        {
            ["/v3/registration-semver2/"] = "https://api.nuget.org/v3/registration5-gz-semver2/",
            ["/v3/registration-gz-semver1/"] = "https://api.nuget.org/v3/registration5-gz-semver1/",
            ["/v3/registration-semver1/"] = "https://api.nuget.org/v3/registration5/",
        }, store, new KeyedAsyncLock(NullLogger<KeyedAsyncLock>.Instance));

        await forwarder.RegistrationProxyAsync(TestContextFactory.MakeGetContext(), "/v3/registration-semver2/", "semver2", Path);
        await forwarder.RegistrationProxyAsync(TestContextFactory.MakeGetContext(), "/v3/registration-gz-semver1/", "gz-semver1", Path);
        await forwarder.RegistrationProxyAsync(TestContextFactory.MakeGetContext(), "/v3/registration-semver1/", "semver1", Path);

        Assert.Contains("$registration/semver2/newtonsoft.json/index.json", store.WrittenKeys);
        Assert.Contains("$registration/gz-semver1/newtonsoft.json/index.json", store.WrittenKeys);
        Assert.Contains("$registration/semver1/newtonsoft.json/index.json", store.WrittenKeys);
        Assert.Equal(3, store.WrittenKeys.Count);
    }

    // ── ForwardMap key not found → stale fallback or 404 ────────────────────

    [Fact]
    public async Task RegistrationProxy_RouteNotFoundInForwardMap_WithPriorCache_ServesStaleFallback()
    {
        byte[] bodyBytes = Encoding.UTF8.GetBytes(RawUpstreamJson);
        var registrationOptions = new RegistrationCacheOptions();
        DateTimeOffset staleTime = DateTimeOffset.UtcNow - registrationOptions.CacheTtl - TimeSpan.FromSeconds(10);

        var store = new FixedContentStore(new CachedContent
        {
            Stream = new MemoryStream(bodyBytes),
            Length = bodyBytes.Length,
            ContentType = "application/json",
            StoredAtUtc = staleTime, // stale
        });
        MirrorOptions options = CreateCacheEnabledOptions();
        // ForwardMap does not contain RoutePrefix — ForwardMap entry is missing.
        Forwarder forwarder = CreateForwarder(options, new StubUpstreamHandler(), [], store, new KeyedAsyncLock(NullLogger<KeyedAsyncLock>.Instance));
        DefaultHttpContext context = TestContextFactory.MakeGetContext();

        await forwarder.RegistrationProxyAsync(context, RoutePrefix, Flavor, Path);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task RegistrationProxy_RouteNotFoundInForwardMap_NoPriorCache_Returns502()
    {
        var store = new NoOpStore();
        MirrorOptions options = CreateCacheEnabledOptions();
        Forwarder forwarder = CreateForwarder(options, new StubUpstreamHandler(), [], store, new KeyedAsyncLock(NullLogger<KeyedAsyncLock>.Instance));
        DefaultHttpContext context = TestContextFactory.MakeGetContext();

        await forwarder.RegistrationProxyAsync(context, RoutePrefix, Flavor, Path);

        Assert.Equal(StatusCodes.Status502BadGateway, context.Response.StatusCode);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static MirrorOptions CreateCacheEnabledOptions()
    {
        return new MirrorOptions
        {
            Cache = { Enabled = true, Registration = { Enabled = true } },
            PublicBaseUrl = "http://localhost",
        };
    }

    private static string ReadResponseBody(DefaultHttpContext context)
    {
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8, leaveOpen: true);
        return reader.ReadToEnd();
    }

    private static Forwarder CreateForwarderWithCache(
        MirrorOptions options, IPackageContentStore store)
    {
        IOptions<MirrorOptions> optionsWrapper = Microsoft.Extensions.Options.Options.Create(options);
        var handler = new StubUpstreamHandler();
        var httpClient = new TestHttpClientFactory(handler);
        var upstreamClient = new UpstreamClient(httpClient, optionsWrapper, NullLogger<UpstreamClient>.Instance, new MirrorMetrics());
        var discovery = new DiscoveryCache(upstreamClient, optionsWrapper, NullLogger<DiscoveryCache>.Instance, new MirrorMetrics(), TimeProvider.System);
        var keyedLock = new KeyedAsyncLock(NullLogger<KeyedAsyncLock>.Instance);

        return new Forwarder(upstreamClient, discovery, optionsWrapper, NullLogger<Forwarder>.Instance, new MirrorMetrics(), store, keyedLock);
    }

    private static Forwarder CreateForwarderWithDiscovery(
        MirrorOptions options, StubUpstreamHandler handler, IPackageContentStore store, KeyedAsyncLock? keyedLock)
    {
        return CreateForwarder(options, handler, new Dictionary<string, string>
        {
            [RoutePrefix] = UpstreamBase,
        }, store, keyedLock);
    }

    private static Forwarder CreateForwarder(
        MirrorOptions options, StubUpstreamHandler handler, Dictionary<string, string> forwardMap,
        IPackageContentStore store, KeyedAsyncLock? keyedLock)
    {
        IOptions<MirrorOptions> optionsWrapper = Microsoft.Extensions.Options.Options.Create(options);
        var httpClient = new TestHttpClientFactory(handler);
        var upstreamClient = new UpstreamClient(httpClient, optionsWrapper, NullLogger<UpstreamClient>.Instance, new MirrorMetrics());

        var cache = new DiscoveryCache(upstreamClient, optionsWrapper, NullLogger<DiscoveryCache>.Instance, new MirrorMetrics(), TimeProvider.System);
        var snapshot = new DiscoverySnapshot(
            DateTimeOffset.UtcNow,
            "{}",
            forwardMap,
            [new RewritePair(UpstreamBase, RoutePrefix),
             new RewritePair("https://api.nuget.org/v3-flatcontainer/", "/v3-flatcontainer/")]);
        SeedSnapshot(cache, snapshot);

        return new Forwarder(upstreamClient, cache, optionsWrapper, NullLogger<Forwarder>.Instance, new MirrorMetrics(), store, keyedLock);
    }

    private static Forwarder CreateForwarderWithEmptyDiscovery(MirrorOptions options, IPackageContentStore store)
    {
        IOptions<MirrorOptions> optionsWrapper = Microsoft.Extensions.Options.Options.Create(options);
        var handler = new StubUpstreamHandler();
        var httpClient = new TestHttpClientFactory(handler);
        var upstreamClient = new UpstreamClient(httpClient, optionsWrapper, NullLogger<UpstreamClient>.Instance, new MirrorMetrics());
        var discovery = new DiscoveryCache(upstreamClient, optionsWrapper, NullLogger<DiscoveryCache>.Instance, new MirrorMetrics(), TimeProvider.System);
        // Not seeded — CurrentSnapshot will throw InvalidOperationException.
        var keyedLock = new KeyedAsyncLock(NullLogger<KeyedAsyncLock>.Instance);

        return new Forwarder(upstreamClient, discovery, optionsWrapper, NullLogger<Forwarder>.Instance, new MirrorMetrics(), store, keyedLock);
    }

    private static void SeedSnapshot(DiscoveryCache cache, DiscoverySnapshot snapshot)
    {
        FieldInfo field = typeof(DiscoveryCache).GetField(
            "_current",
            BindingFlags.NonPublic | BindingFlags.Instance)
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

    private sealed class RecordingStore(bool returnsCacheHit) : IPackageContentStore
    {
        public bool WasWritten { get; private set; }

        public string? LastWrittenKey { get; private set; }

        public List<string> WrittenKeys { get; } = [];

        public ValueTask<CachedContent?> TryGetAsync(string key, CancellationToken ct)
        {
            if (returnsCacheHit)
            {
                var stream = new MemoryStream("cached-json"u8.ToArray());
                return ValueTask.FromResult<CachedContent?>(new CachedContent
                {
                    Stream = stream,
                    Length = 11,
                    ContentType = "application/json",
                    StoredAtUtc = DateTimeOffset.UtcNow,
                });
            }

            return ValueTask.FromResult<CachedContent?>(null);
        }

        public ValueTask<ICacheWriteHandle> BeginWriteAsync(string key, CancellationToken ct)
        {
            LastWrittenKey = key;
            WrittenKeys.Add(key);
            return ValueTask.FromResult<ICacheWriteHandle>(new RecordingWriteHandle(
                bytes => WasWritten = bytes > 0));
        }

        private sealed class RecordingWriteHandle(Action<int> onCommit) : ICacheWriteHandle
        {
            private readonly MemoryStream _stream = new();

            public Stream Stream => _stream;

            public void SetMetadata(string contentType, long length, string? etag)
            {
            }

            public ValueTask CommitAsync(CancellationToken ct)
            {
                onCommit((int)_stream.Length);
                return ValueTask.CompletedTask;
            }

            public async ValueTask DisposeAsync()
            {
                await _stream.DisposeAsync();
            }
        }
    }
}
