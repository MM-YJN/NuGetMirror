using System.Net;
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

/// <summary>
/// Tests the URL rewriting behavior of repository signatures index documents.
/// Exercises <see cref="UrlRewriter"/> with the rewrite pairs produced by
/// <see cref="DiscoveryCache"/> to verify <c>contentUrl</c> values are redirected
/// through the mirror prefix.
/// </summary>
public sealed class RepositorySignaturesForwarderTests
{
    [Fact]
    public async Task IndexHandler_OversizedStream_IsNotCached()
    {
        using var stream = new GeneratedBodyStream(long.MaxValue);
        using StubUpstreamHandler handler = new StubUpstreamHandler().MapFactory(
            "http://upstream.example/v3-index/repository-signatures/5.0.0/index.json",
            () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
        var store = new InMemoryStore { ReturnsNull = true };
        IOptions<MirrorOptions> options = Microsoft.Extensions.Options.Options.Create(new MirrorOptions
        {
            RepositorySignatures = { Enabled = true },
            Cache = { Enabled = true, RepositorySignatures = { Enabled = true, MaxBodyBytes = 32 } },
        });
        RepositorySignaturesForwarder forwarder = CreateForwarderForPipeline(store, handler, options);
        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        await forwarder.IndexHandlerAsync(context, "5.0.0");
        Assert.Equal(StatusCodes.Status502BadGateway, context.Response.StatusCode);
        Assert.Equal(33, stream.BytesRead);
        Assert.True(stream.Disposed);
        Assert.Null(store.LastWrittenKey);
    }

    [Fact]
    public void RewriteRepositorySignaturesIndex_RewritesContentUrls()
    {
        string index = """
        {
          "allRepositorySigned": true,
          "signingCertificates": [
            {
              "fingerprints": {
                "2.16.840.1.101.3.4.2.1": "0e5f38f57dc1bcc806d8494f4f90fbcedd988b46760709cbeec6f4219aa6157d"
              },
              "subject": "CN=NuGet.org",
              "issuer": "CN=DigiCert",
              "notBefore": "2018-04-10T00:00:00.0000000Z",
              "notAfter": "2021-04-14T12:00:00.0000000Z",
              "contentUrl": "https://api.nuget.org/v3-index/repository-signatures/certificates/abc.crt"
            }
          ]
        }
        """;

        var pairs = new List<RewritePair>
        {
            new("https://api.nuget.org/v3-index/repository-signatures/", "/v3/repository-signatures/"),
        };

        string result = UrlRewriter.Rewrite(index, pairs, "http://mirror:5049");

        Assert.Contains("http://mirror:5049/v3/repository-signatures/certificates/abc.crt", result, StringComparison.Ordinal);
        Assert.DoesNotContain("https://api.nuget.org/v3-index/repository-signatures/", result, StringComparison.Ordinal);
    }

    [Fact]
    public void RewriteRepositorySignaturesIndex_MultipleCertificates_RewritesAll()
    {
        string index = """
        {
          "allRepositorySigned": true,
          "signingCertificates": [
            {
              "fingerprints": { "2.16.840.1.101.3.4.2.1": "aaa" },
              "subject": "CN=First",
              "issuer": "CN=Issuer",
              "notBefore": "2020-01-01T00:00:00Z",
              "notAfter": "2021-01-01T00:00:00Z",
              "contentUrl": "https://api.nuget.org/v3-index/repository-signatures/certificates/first.crt"
            },
            {
              "fingerprints": { "2.16.840.1.101.3.4.2.1": "bbb" },
              "subject": "CN=Second",
              "issuer": "CN=Issuer",
              "notBefore": "2021-01-01T00:00:00Z",
              "notAfter": "2022-01-01T00:00:00Z",
              "contentUrl": "https://api.nuget.org/v3-index/repository-signatures/certificates/second.crt"
            }
          ]
        }
        """;

        var pairs = new List<RewritePair>
        {
            new("https://api.nuget.org/v3-index/repository-signatures/", "/v3/repository-signatures/"),
        };

        string result = UrlRewriter.Rewrite(index, pairs, "http://mirror:5049");

        Assert.Contains("http://mirror:5049/v3/repository-signatures/certificates/first.crt", result, StringComparison.Ordinal);
        Assert.Contains("http://mirror:5049/v3/repository-signatures/certificates/second.crt", result, StringComparison.Ordinal);
        Assert.DoesNotContain("https://api.nuget.org/v3-index/repository-signatures/", result, StringComparison.Ordinal);
    }

    [Fact]
    public void RewriteRepositorySignaturesIndex_NoMatchingPrefix_ReturnsOriginal()
    {
        string index = """
        {
          "allRepositorySigned": false,
          "signingCertificates": [
            {
              "fingerprints": { "2.16.840.1.101.3.4.2.1": "abc" },
              "subject": "CN=Other",
              "issuer": "CN=Other",
              "notBefore": "2020-01-01T00:00:00Z",
              "notAfter": "2021-01-01T00:00:00Z",
              "contentUrl": "https://other.example.com/certs/abc.crt"
            }
          ]
        }
        """;

        var pairs = new List<RewritePair>
        {
            new("https://api.nuget.org/v3-index/repository-signatures/", "/v3/repository-signatures/"),
        };

        string result = UrlRewriter.Rewrite(index, pairs, "http://mirror:5049");

        Assert.Contains("https://other.example.com/certs/abc.crt", result, StringComparison.Ordinal);
        Assert.DoesNotContain("http://mirror:5049", result, StringComparison.Ordinal);
    }

    // ── CachedProxyPipeline: invalid cache-entry length guard ─────────────────

    [Fact]
    public async Task IndexHandler_Returns500_WhenCachedLengthIsNegative()
    {
        // CachedContent.Length = -1 exercises the guard in ServeRewriteIndexFromCacheAsync
        // that protects the int cast.  A negative length can occur if the store returns
        // a CachedContent without valid metadata (e.g. corrupted .meta file that omits
        // the length field, or a store implementation that initialises Length to -1).
        var options = new MirrorOptions
        {
            RepositorySignatures = { Enabled = true },
            Cache =
            {
                Enabled = true,
                RepositorySignatures = { Enabled = true },
            },
        };
        IOptions<MirrorOptions> optionsWrapper = Microsoft.Extensions.Options.Options.Create(options);
        var store = new FixedContentStore(new CachedContent
        {
            Stream = new MemoryStream(),       // empty stream — never read due to the guard
            Length = -1,                       // triggers the guard → HTTP 500
            ContentType = "application/json",
            StoredAtUtc = DateTimeOffset.UtcNow, // makes IsCacheFresh return true
        });

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        RepositorySignaturesForwarder forwarder = CreateForwarderWith(optionsWrapper, store);

        await forwarder.IndexHandlerAsync(context, "5.0.0");

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
    }

    // ── RepositorySignaturesForwarder: null-snapshot rewrite path ─────────────

    [Fact]
    public async Task IndexHandler_ServesCachedBodyUnrewritten_WhenDiscoverySnapshotUnavailable()
    {
        // When TryGetSnapshot returns null (discovery has not yet completed), the
        // RewriteBody delegate must return the body as-is rather than crashing.
        // The cached body — containing raw upstream URLs — is served without rewriting.
        const string RawBody = """{"contentUrl":"https://api.nuget.org/v3-index/repository-signatures/certificates/test.crt"}""";
        byte[] bodyBytes = Encoding.UTF8.GetBytes(RawBody);

        var options = new MirrorOptions
        {
            RepositorySignatures = { Enabled = true },
            Cache =
            {
                Enabled = true,
                RepositorySignatures = { Enabled = true },
            },
        };
        IOptions<MirrorOptions> optionsWrapper = Microsoft.Extensions.Options.Options.Create(options);
        var store = new FixedContentStore(new CachedContent
        {
            Stream = new MemoryStream(bodyBytes),
            Length = bodyBytes.Length,
            ContentType = "application/json",
            StoredAtUtc = DateTimeOffset.UtcNow, // fresh → served from cache
        });

        DefaultHttpContext context = TestContextFactory.MakeGetContext();

        // Use a DiscoveryCache with no snapshot seeded so CurrentSnapshot throws.
        RepositorySignaturesForwarder forwarder = CreateForwarderWithEmptyDiscovery(optionsWrapper, store);

        await forwarder.IndexHandlerAsync(context, "5.0.0");

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8, leaveOpen: true);
        string responseBody = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);

        // Body should be the raw (unrewritten) cache content — upstream URLs intact.
        Assert.Equal(RawBody, responseBody);
        Assert.Contains("api.nuget.org", responseBody, StringComparison.Ordinal);
    }

    // ── Existing handler tests ────────────────────────────────────────────────

    [Fact]
    public async Task IndexHandler_Returns404_WhenRepositorySignaturesDisabled()
    {
        var context = new DefaultHttpContext();
        RepositorySignaturesForwarder forwarder = CreateForwarder(enabled: false);

        await forwarder.IndexHandlerAsync(context, "5.0.0");

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
    }

    [Fact]
    public async Task CertificateHandler_Returns404_WhenRepositorySignaturesDisabled()
    {
        var context = new DefaultHttpContext();
        RepositorySignaturesForwarder forwarder = CreateForwarder(enabled: false);

        await forwarder.CertificateHandlerAsync(context, "abc.crt");

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
    }

    [Fact]
    public async Task CertificateHandler_Returns404_WhenCacheDisabled()
    {
        var context = new DefaultHttpContext();
        var options = new MirrorOptions
        {
            RepositorySignatures = { Enabled = true },
            Cache =
            {
                Enabled = false,
                RepositorySignatures = { Enabled = true },
            },
        };
        IOptions<MirrorOptions> optionsWrapper = Microsoft.Extensions.Options.Options.Create(options);
        RepositorySignaturesForwarder forwarder = CreateForwarderWith(optionsWrapper, new NoOpStore());

        await forwarder.CertificateHandlerAsync(context, "abc.crt");

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
    }

    [Fact]
    public async Task CertificateHandler_Returns404_WhenReposignCacheDisabled()
    {
        var context = new DefaultHttpContext();
        var options = new MirrorOptions
        {
            RepositorySignatures = { Enabled = true },
            Cache =
            {
                Enabled = true,
                RepositorySignatures = { Enabled = false },
            },
        };
        IOptions<MirrorOptions> optionsWrapper = Microsoft.Extensions.Options.Options.Create(options);
        RepositorySignaturesForwarder forwarder = CreateForwarderWith(optionsWrapper, new NoOpStore());

        await forwarder.CertificateHandlerAsync(context, "abc.crt");

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("../x.crt")]
    [InlineData("a/../b.crt")]
    [InlineData("a\\b.crt")]
    [InlineData("foo..bar.crt")]
    public async Task CertificateHandler_Returns404_ForInvalidPath(string path)
    {
        var options = new MirrorOptions
        {
            RepositorySignatures = { Enabled = true },
            Cache =
            {
                Enabled = true,
                RepositorySignatures = { Enabled = true },
            },
        };
        IOptions<MirrorOptions> optionsWrapper = Microsoft.Extensions.Options.Options.Create(options);
        var store = new InMemoryStore();
        RepositorySignaturesForwarder forwarder = CreateForwarderWith(optionsWrapper, store);

        var context = new DefaultHttpContext();
        await forwarder.CertificateHandlerAsync(context, path);

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Null(store.LastWrittenKey);
    }

    [Fact]
    public async Task CertificateHandler_AcceptsValidPath_FetchesFromUpstream()
    {
        byte[] certBytes = "test-certificate-content"u8.ToArray();
        var store = new InMemoryStore { ReturnsNull = true };
        var handler = new StubUpstreamHandler();
        const string UpstreamUrl = "http://upstream.example/v3-index/repository-signatures/certificates/abc.crt";
        handler.MapBytes(UpstreamUrl, certBytes, "application/octet-stream");
        var options = new MirrorOptions
        {
            RepositorySignatures = { Enabled = true },
            Cache =
            {
                Enabled = true,
                RepositorySignatures = { Enabled = true },
            },
        };
        IOptions<MirrorOptions> optionsWrapper = Microsoft.Extensions.Options.Options.Create(options);
        RepositorySignaturesForwarder forwarder = CreateForwarderForPipeline(store, handler, optionsWrapper);

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        await forwarder.CertificateHandlerAsync(context, "abc.crt");

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("application/octet-stream", context.Response.ContentType);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var ms = new MemoryStream();
        await context.Response.Body.CopyToAsync(ms, TestContext.Current.CancellationToken);
        Assert.Equal(certBytes, ms.ToArray());

        Assert.Equal(1, handler.GetCount(UpstreamUrl));
        Assert.Equal("$reposign/certificates/abc.crt", store.LastWrittenKey); // confirms CacheContentType=RepositorySignatureCertificate via the $reposign/ cache-key namespace
    }

    [Fact]
    public async Task CertificateHandler_HeadRequest_ReturnsHeadersWithoutBody()
    {
        byte[] certBytes = "head-certificate-content"u8.ToArray();
        var store = new InMemoryStore { CachedBytes = certBytes };
        var handler = new StubUpstreamHandler();
        var options = new MirrorOptions
        {
            RepositorySignatures = { Enabled = true },
            Cache =
            {
                Enabled = true,
                RepositorySignatures = { Enabled = true },
            },
        };
        IOptions<MirrorOptions> optionsWrapper = Microsoft.Extensions.Options.Options.Create(options);
        RepositorySignaturesForwarder forwarder = CreateForwarderForPipeline(store, handler, optionsWrapper);

        DefaultHttpContext context = TestContextFactory.MakeHeadContext();
        await forwarder.CertificateHandlerAsync(context, "abc.crt");

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("application/octet-stream", context.Response.ContentType);
        Assert.Equal(certBytes.Length, context.Response.ContentLength);

        // Body must be empty for HEAD requests (SupportsHead + IsHead skip CopyToAsync)
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        Assert.Equal(0, context.Response.Body.Length);
    }

    [Fact]
    public async Task CertificateHandler_ServesFromCache_WhenEntryFresh()
    {
        byte[] certBytes = "cached-certificate-data"u8.ToArray();
        var store = new InMemoryStore { CachedBytes = certBytes };
        var handler = new StubUpstreamHandler();
        var options = new MirrorOptions
        {
            RepositorySignatures = { Enabled = true },
            Cache =
            {
                Enabled = true,
                RepositorySignatures = { Enabled = true },
            },
        };
        IOptions<MirrorOptions> optionsWrapper = Microsoft.Extensions.Options.Options.Create(options);
        RepositorySignaturesForwarder forwarder = CreateForwarderForPipeline(store, handler, optionsWrapper);

        DefaultHttpContext context = TestContextFactory.MakeGetContext();
        await forwarder.CertificateHandlerAsync(context, "abc.crt");

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(certBytes.Length, context.Response.ContentLength);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var ms = new MemoryStream();
        await context.Response.Body.CopyToAsync(ms, TestContext.Current.CancellationToken);
        Assert.Equal(certBytes, ms.ToArray());

        // Upstream should NOT be hit — served from cache
        Assert.Equal(0, handler.GetCount("http://upstream.example/v3-index/repository-signatures/certificates/abc.crt"));
    }

    [Fact]
    public async Task CertificateHandler_NullContext_Throws()
    {
        var options = new MirrorOptions
        {
            RepositorySignatures = { Enabled = true },
            Cache =
            {
                Enabled = true,
                RepositorySignatures = { Enabled = true },
            },
        };
        IOptions<MirrorOptions> optionsWrapper = Microsoft.Extensions.Options.Options.Create(options);
        RepositorySignaturesForwarder forwarder = CreateForwarderWith(optionsWrapper, new NoOpStore());

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            forwarder.CertificateHandlerAsync(null!, "abc.crt"));
    }

    [Fact]
    public async Task CertificateHandler_NullPath_Throws()
    {
        var options = new MirrorOptions
        {
            RepositorySignatures = { Enabled = true },
            Cache =
            {
                Enabled = true,
                RepositorySignatures = { Enabled = true },
            },
        };
        IOptions<MirrorOptions> optionsWrapper = Microsoft.Extensions.Options.Options.Create(options);
        RepositorySignaturesForwarder forwarder = CreateForwarderWith(optionsWrapper, new NoOpStore());

        var context = new DefaultHttpContext();
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            forwarder.CertificateHandlerAsync(context, null!));
    }

    private static RepositorySignaturesForwarder CreateForwarder(bool enabled)
    {
        var options = new MirrorOptions
        {
            RepositorySignatures = { Enabled = enabled },
            Cache = { RepositorySignatures = { Enabled = true } },
        };
        IOptions<MirrorOptions> optionsWrapper = Microsoft.Extensions.Options.Options.Create(options);
        var handler = new StubUpstreamHandler();
        var httpClient = new TestHttpClientFactory(handler);
        var upstreamClient = new UpstreamClient(httpClient, optionsWrapper, NullLogger<UpstreamClient>.Instance, new MirrorMetrics());
        var discovery = new DiscoveryCache(upstreamClient, optionsWrapper, NullLogger<DiscoveryCache>.Instance, new MirrorMetrics(), TimeProvider.System);
        var keyedLock = new KeyedAsyncLock(NullLogger<KeyedAsyncLock>.Instance);
        var metrics = new MirrorMetrics();

        return new RepositorySignaturesForwarder(
            upstreamClient,
            discovery,
            new NoOpStore(),
            keyedLock,
            optionsWrapper,
            NullLogger<RepositorySignaturesForwarder>.Instance,
            metrics,
            TimeProvider.System);
    }

    /// <summary>Creates a forwarder backed by a specific store and options monitor.</summary>
    private static RepositorySignaturesForwarder CreateForwarderWith(
        IOptions<MirrorOptions> optionsWrapper, IPackageContentStore store)
    {
        var handler = new StubUpstreamHandler();
        var httpClient = new TestHttpClientFactory(handler);
        var upstreamClient = new UpstreamClient(httpClient, optionsWrapper, NullLogger<UpstreamClient>.Instance, new MirrorMetrics());
        var discovery = new DiscoveryCache(upstreamClient, optionsWrapper, NullLogger<DiscoveryCache>.Instance, new MirrorMetrics(), TimeProvider.System);
        var keyedLock = new KeyedAsyncLock(NullLogger<KeyedAsyncLock>.Instance);

        return new RepositorySignaturesForwarder(
            upstreamClient,
            discovery,
            store,
            keyedLock,
            optionsWrapper,
            NullLogger<RepositorySignaturesForwarder>.Instance,
            new MirrorMetrics(),
            TimeProvider.System);
    }

    /// <summary>
    /// Creates a forwarder whose <see cref="DiscoveryCache"/> has not been seeded, so
    /// <c>CurrentSnapshot</c> throws <see cref="InvalidOperationException"/>.
    /// </summary>
    private static RepositorySignaturesForwarder CreateForwarderWithEmptyDiscovery(
        IOptions<MirrorOptions> optionsWrapper, IPackageContentStore store)
    {
        // Use a handler that always fails so GetAsync is never called
        // (the cache is populated, so RunCachedRewriteIndexAsync never reaches discovery.GetAsync).
        var handler = new StubUpstreamHandler();
        var httpClient = new TestHttpClientFactory(handler);
        var upstreamClient = new UpstreamClient(httpClient, optionsWrapper, NullLogger<UpstreamClient>.Instance, new MirrorMetrics());

        // Do NOT seed the discovery cache — CurrentSnapshot will throw InvalidOperationException.
        var discovery = new DiscoveryCache(upstreamClient, optionsWrapper, NullLogger<DiscoveryCache>.Instance, new MirrorMetrics(), TimeProvider.System);
        var keyedLock = new KeyedAsyncLock(NullLogger<KeyedAsyncLock>.Instance);

        return new RepositorySignaturesForwarder(
            upstreamClient,
            discovery,
            store,
            keyedLock,
            optionsWrapper,
            NullLogger<RepositorySignaturesForwarder>.Instance,
            new MirrorMetrics(),
            TimeProvider.System);
    }

    /// <summary>
    /// Creates a forwarder with a seeded <see cref="DiscoveryCache"/> so the streaming
    /// pipeline can resolve the upstream base for /v3/repository-signatures/.
    /// </summary>
    private static RepositorySignaturesForwarder CreateForwarderForPipeline(
        IPackageContentStore store, HttpMessageHandler handler, IOptions<MirrorOptions> optionsWrapper)
    {
        var httpClient = new TestHttpClientFactory(handler);
        var upstreamClient = new UpstreamClient(httpClient, optionsWrapper, NullLogger<UpstreamClient>.Instance, new MirrorMetrics());
        var discovery = new DiscoveryCache(upstreamClient, optionsWrapper, NullLogger<DiscoveryCache>.Instance, new MirrorMetrics(), TimeProvider.System);
        DiscoveryCacheSeeder.SeedSnapshot(discovery, new Dictionary<string, string>
        {
            ["/v3/repository-signatures/"] = "http://upstream.example/v3-index/repository-signatures/",
        });
        var keyedLock = new KeyedAsyncLock(NullLogger<KeyedAsyncLock>.Instance);

        return new RepositorySignaturesForwarder(
            upstreamClient,
            discovery,
            store,
            keyedLock,
            optionsWrapper,
            NullLogger<RepositorySignaturesForwarder>.Instance,
            new MirrorMetrics(),
            TimeProvider.System);
    }

    private sealed class NoOpStore : IPackageContentStore
    {
        public ValueTask<CachedContent?> TryGetAsync(string key, CancellationToken ct) => ValueTask.FromResult<CachedContent?>(null);

        public ValueTask<ICacheWriteHandle> BeginWriteAsync(string key, CancellationToken ct) => throw new NotSupportedException();
    }

    /// <summary>A store that always returns the same pre-built <see cref="CachedContent"/>.</summary>
    private sealed class FixedContentStore(CachedContent content) : IPackageContentStore
    {
        public ValueTask<CachedContent?> TryGetAsync(string key, CancellationToken ct)
            => ValueTask.FromResult<CachedContent?>(content);

        public ValueTask<ICacheWriteHandle> BeginWriteAsync(string key, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
