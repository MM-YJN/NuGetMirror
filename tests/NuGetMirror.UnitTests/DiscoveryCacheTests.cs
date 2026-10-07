using System.Net;
using System.Reflection;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using NuGetMirror.Configuration;
using NuGetMirror.Diagnostics;
using NuGetMirror.Discovery;
using NuGetMirror.TestKit;
using NuGetMirror.Upstream;

namespace NuGetMirror.UnitTests;

public sealed class DiscoveryCacheTests
{
    [Fact]
    public async Task BuildsForwardMap_CorrectKeys()
    {
        DiscoverySnapshot snapshot = await DiscoverFromFixtureAsync(TestFixtures.NuGetOrgServiceIndex);

        Assert.True(snapshot.ForwardMap.ContainsKey("/v3-flatcontainer/"));
        Assert.True(snapshot.ForwardMap.ContainsKey("/v3/registration-semver2/"));
        Assert.True(snapshot.ForwardMap.ContainsKey("/v3/registration-gz-semver1/"));
        Assert.True(snapshot.ForwardMap.ContainsKey("/v3/registration-semver1/"));
        Assert.True(snapshot.ForwardMap.ContainsKey("/v3/search"));
        Assert.True(snapshot.ForwardMap.ContainsKey("/v3/autocomplete"));
        Assert.True(snapshot.ForwardMap.ContainsKey("/v3/catalog0/"));
        Assert.True(snapshot.ForwardMap.ContainsKey("/v3/vulnerability/"));
        Assert.True(snapshot.ForwardMap.ContainsKey("/v3/repository-signatures/"));
    }

    [Fact]
    public async Task ForwardMap_HasCorrectValues_ForNuGetOrg()
    {
        DiscoverySnapshot snapshot = await DiscoverFromFixtureAsync(TestFixtures.NuGetOrgServiceIndex);

        Assert.Equal("https://api.nuget.org/v3-flatcontainer/", snapshot.ForwardMap["/v3-flatcontainer/"]);
        Assert.Equal("https://api.nuget.org/v3/registration5-gz-semver2/", snapshot.ForwardMap["/v3/registration-semver2/"]);
        Assert.Equal("https://api.nuget.org/v3/registration5-gz-semver1/", snapshot.ForwardMap["/v3/registration-gz-semver1/"]);
        Assert.Equal("https://api.nuget.org/v3/registration5/", snapshot.ForwardMap["/v3/registration-semver1/"]);
        Assert.Equal("https://azuresearch-usnc.nuget.org/query", snapshot.ForwardMap["/v3/search"]);
        Assert.Equal("https://azuresearch-usnc.nuget.org/autocomplete", snapshot.ForwardMap["/v3/autocomplete"]);
        Assert.Equal("https://api.nuget.org/v3/catalog0/", snapshot.ForwardMap["/v3/catalog0/"]);
        Assert.Equal("https://api.nuget.org/v3/vulnerabilities/", snapshot.ForwardMap["/v3/vulnerability/"]);
        Assert.Equal("https://api.nuget.org/v3-index/repository-signatures/", snapshot.ForwardMap["/v3/repository-signatures/"]);
    }

    [Fact]
    public async Task IndexRewriting_TurnsUpstreamUrlsIntoMirrorUrls()
    {
        DiscoverySnapshot snapshot = await DiscoverFromFixtureAsync(TestFixtures.NuGetOrgServiceIndex);
        string rewritten = UrlRewriter.Rewrite(TestFixtures.NuGetOrgServiceIndex, snapshot.RewritePairs, "http://localhost:5049");

        Assert.Contains("http://localhost:5049/v3-flatcontainer/", rewritten, StringComparison.Ordinal);
        Assert.Contains("http://localhost:5049/v3/registration-semver2/", rewritten, StringComparison.Ordinal);
        Assert.Contains("http://localhost:5049/v3/registration-gz-semver1/", rewritten, StringComparison.Ordinal);
        Assert.Contains("http://localhost:5049/v3/registration-semver1/", rewritten, StringComparison.Ordinal);

        // Search and autocomplete URLs should be rewritten to the mirror
        Assert.Contains("http://localhost:5049/v3/search", rewritten, StringComparison.Ordinal);
        Assert.Contains("http://localhost:5049/v3/autocomplete", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("azuresearch-usnc.nuget.org", rewritten, StringComparison.Ordinal);

        // Catalog URL should be rewritten to the mirror
        Assert.Contains("http://localhost:5049/v3/catalog0/index.json", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("api.nuget.org/v3/catalog0", rewritten, StringComparison.Ordinal);

        Assert.Contains("http://localhost:5049/v3/vulnerability/index.json", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("api.nuget.org/v3/vulnerabilities/index.json", rewritten, StringComparison.Ordinal);

        Assert.Contains("http://localhost:5049/v3/repository-signatures/4.7.0/index.json", rewritten, StringComparison.Ordinal);
        Assert.Contains("http://localhost:5049/v3/repository-signatures/5.0.0/index.json", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("api.nuget.org/v3-index/repository-signatures", rewritten, StringComparison.Ordinal);

        // ReadmeUriTemplate on a different CDN should be rewritten to the mirror flat-container base.
        Assert.Contains("http://localhost:5049/v3-flatcontainer/{lower_id}/{lower_version}/readme", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("globalcdn.nuget.org", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MultipleSearchHosts_AllRewritten_ForwardsToPrimary()
    {
        const string MultiHostIndex = """
        {
          "version": "3.0.0",
          "resources": [
            {
              "@id": "https://search-primary.example.com/query",
              "@type": "SearchQueryService"
            },
            {
              "@id": "https://search-secondary.example.com/query",
              "@type": "SearchQueryService/3.5.0"
            },
            {
              "@id": "https://search-primary.example.com/autocomplete",
              "@type": "SearchAutocompleteService"
            },
            {
              "@id": "https://search-secondary.example.com/autocomplete",
              "@type": "SearchAutocompleteService/3.5.0"
            }
          ]
        }
        """;

        DiscoverySnapshot snapshot = await DiscoverFromFixtureAsync(MultiHostIndex);

        // Forwarding uses the first (primary) endpoint.
        Assert.Equal("https://search-primary.example.com/query", snapshot.ForwardMap["/v3/search"]);
        Assert.Equal("https://search-primary.example.com/autocomplete", snapshot.ForwardMap["/v3/autocomplete"]);

        // Every distinct upstream host @id is rewritten to the mirror prefix so no
        // secondary endpoint leaks through the rewritten service index.
        string rewritten = UrlRewriter.Rewrite(MultiHostIndex, snapshot.RewritePairs, "http://localhost:5049");
        Assert.DoesNotContain("search-primary.example.com", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("search-secondary.example.com", rewritten, StringComparison.Ordinal);
        Assert.Contains("http://localhost:5049/v3/search", rewritten, StringComparison.Ordinal);
        Assert.Contains("http://localhost:5049/v3/autocomplete", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadmeUriTemplate_DifferentCdnHost_IsRewrittenToFlatContainer()
    {
        // The NuGet.org service index advertises ReadmeUriTemplate on globalcdn.nuget.org —
        // a different CDN domain than the PackageBaseAddress on api.nuget.org.
        // The mirror should rewrite the CDN prefix to its own flat-container base.
        DiscoverySnapshot snapshot = await DiscoverFromFixtureAsync(TestFixtures.NuGetOrgServiceIndex);
        string rewritten = UrlRewriter.Rewrite(TestFixtures.NuGetOrgServiceIndex, snapshot.RewritePairs, "http://localhost:5049");

        Assert.Contains("http://localhost:5049/v3-flatcontainer/{lower_id}/{lower_version}/readme", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("globalcdn.nuget.org", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadmeUriTemplate_SameHostAsPackageBaseAddress_NotDuplicated()
    {
        // When ReadmeUriTemplate shares the same host/prefix as PackageBaseAddress,
        // no duplicate RewritePair should be added for that prefix.
        const string SameHostIndex = """
        {
          "version": "3.0.0",
          "resources": [
            {
              "@id": "https://api.example.com/v3-flatcontainer/",
              "@type": "PackageBaseAddress/3.0.0"
            },
            {
              "@id": "https://api.example.com/v3-flatcontainer/{lower_id}/{lower_version}/readme",
              "@type": "ReadmeUriTemplate/6.13.0"
            }
          ]
        }
        """;

        DiscoverySnapshot snapshot = await DiscoverFromFixtureAsync(SameHostIndex);

        // Only one RewritePair for the flat-container prefix (no duplicate).
        var flatContainerPairs = snapshot.RewritePairs
            .Where(p => string.Equals(p.MirrorPrefix, "/v3-flatcontainer/", StringComparison.Ordinal))
            .ToList();
        Assert.Single(flatContainerPairs);

        string rewritten = UrlRewriter.Rewrite(SameHostIndex, snapshot.RewritePairs, "http://localhost:5049");
        Assert.Contains("http://localhost:5049/v3-flatcontainer/{lower_id}/{lower_version}/readme", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("api.example.com", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandlesRewrittenUpstream_PathsAreNonStandard()
    {
        DiscoverySnapshot snapshot = await DiscoverFromFixtureAsync(TestFixtures.RewrittenUpstreamIndex);

        Assert.Equal("https://corp.example.com/x/flat/", snapshot.ForwardMap["/v3-flatcontainer/"]);
        Assert.Equal("https://corp.example.com/x/reg-gz2/", snapshot.ForwardMap["/v3/registration-semver2/"]);
        Assert.Equal("https://corp.example.com/x/reg-gz1/", snapshot.ForwardMap["/v3/registration-gz-semver1/"]);
        Assert.Equal("https://corp.example.com/x/reg/", snapshot.ForwardMap["/v3/registration-semver1/"]);
    }

    [Fact]
    public async Task RewriteIndex_WithRewrittenUpstream_ProducesCorrectMirrorUrls()
    {
        DiscoverySnapshot snapshot = await DiscoverFromFixtureAsync(TestFixtures.RewrittenUpstreamIndex);
        string rewritten = UrlRewriter.Rewrite(TestFixtures.RewrittenUpstreamIndex, snapshot.RewritePairs, "http://localhost:5049");

        Assert.Contains("http://localhost:5049/v3-flatcontainer/", rewritten, StringComparison.Ordinal);
        Assert.Contains("http://localhost:5049/v3/registration-semver2/", rewritten, StringComparison.Ordinal);
        Assert.Contains("http://localhost:5049/v3/registration-gz-semver1/", rewritten, StringComparison.Ordinal);
        Assert.Contains("http://localhost:5049/v3/registration-semver1/", rewritten, StringComparison.Ordinal);

        // Original upstream URLs should not appear
        Assert.DoesNotContain("corp.example.com/x/flat/", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("corp.example.com/x/reg-gz2/", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CatalogNotAdvertised_ForwardMapDoesNotContainCatalogKey()
    {
        DiscoverySnapshot snapshot = await DiscoverFromFixtureAsync(TestFixtures.RewrittenUpstreamIndex);

        Assert.False(snapshot.ForwardMap.ContainsKey("/v3/catalog0/"));
    }

    [Fact]
    public async Task VulnerabilityInfoNotAdvertised_ForwardMapDoesNotContainVulnerabilityKey()
    {
        DiscoverySnapshot snapshot = await DiscoverFromFixtureAsync(TestFixtures.RewrittenUpstreamIndex);

        Assert.False(snapshot.ForwardMap.ContainsKey("/v3/vulnerability/"));
    }

    [Fact]
    public async Task RepositorySignaturesDisabled_ForwardMapDoesNotContainRepoSigKey()
    {
        var options = new MirrorOptions(); // RepositorySignatures.Enabled = false by default
        IOptions<MirrorOptions> optionsWrapper = Microsoft.Extensions.Options.Options.Create(options);
        using var handler = new SuccessHandler(TestFixtures.NuGetOrgServiceIndex);
        var httpClient = new TestHttpClientFactory(handler);
        var client = new UpstreamClient(httpClient, optionsWrapper, NullLogger<UpstreamClient>.Instance, new MirrorMetrics());
        var cache = new DiscoveryCache(client, optionsWrapper, NullLogger<DiscoveryCache>.Instance, new MirrorMetrics(), TimeProvider.System);

        DiscoverySnapshot snapshot = await cache.GetAsync(TestContext.Current.CancellationToken);

        Assert.False(snapshot.ForwardMap.ContainsKey("/v3/repository-signatures/"));

        string rewritten = UrlRewriter.Rewrite(TestFixtures.NuGetOrgServiceIndex, snapshot.RewritePairs, "http://localhost:5049");
        Assert.DoesNotContain("/v3/repository-signatures/", rewritten, StringComparison.Ordinal);
        Assert.Contains("api.nuget.org/v3-index/repository-signatures", rewritten, StringComparison.Ordinal);
    }

    // Regression: when Mirror:RepositorySignatures:Enabled=true but Mirror:Cache:Enabled=false
    // (or Mirror:Cache:RepositorySignatures:Enabled=false), the service index must NOT advertise
    // mirror-local repository-signature URLs that the forwarder cannot serve.
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, true)]
    public async Task RepositorySignatures_NotAdvertised_UnlessAllThreeFlagsEnabled(
        bool repoSigEnabled, bool cacheRepoSigEnabled, bool cacheEnabled)
    {
        var options = new MirrorOptions
        {
            RepositorySignatures = { Enabled = repoSigEnabled },
            Cache = { Enabled = cacheEnabled, RepositorySignatures = { Enabled = cacheRepoSigEnabled } },
        };
        IOptions<MirrorOptions> optionsWrapper = Microsoft.Extensions.Options.Options.Create(options);
        using var handler = new SuccessHandler(TestFixtures.NuGetOrgServiceIndex);
        var httpClient = new TestHttpClientFactory(handler);
        var client = new UpstreamClient(httpClient, optionsWrapper, NullLogger<UpstreamClient>.Instance, new MirrorMetrics());
        var cache = new DiscoveryCache(client, optionsWrapper, NullLogger<DiscoveryCache>.Instance, new MirrorMetrics(), TimeProvider.System);

        DiscoverySnapshot snapshot = await cache.GetAsync(TestContext.Current.CancellationToken);

        Assert.False(snapshot.ForwardMap.ContainsKey("/v3/repository-signatures/"));
        string rewritten = UrlRewriter.Rewrite(TestFixtures.NuGetOrgServiceIndex, snapshot.RewritePairs, "http://localhost:5049");
        Assert.DoesNotContain("/v3/repository-signatures/", rewritten, StringComparison.Ordinal);
        Assert.Contains("api.nuget.org/v3-index/repository-signatures", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StaleCache_ServesLastGoodOnRefreshFailure()
    {
        // This test validates the stale cache pattern:
        // When the TTL is zero and a refresh fails, the DiscoveryCache returns the last good snapshot.
        // We verify this by doing a successful discovery first, then forcing a refresh failure.
        CancellationToken ct = TestContext.Current.CancellationToken;
        var options = new MirrorOptions();
        IOptions<MirrorOptions> optionsWrapper = Microsoft.Extensions.Options.Options.Create(options);
        var httpClient = new TestHttpClientFactory(new SuccessHandler(TestFixtures.NuGetOrgServiceIndex));
        var client = new UpstreamClient(httpClient, optionsWrapper, NullLogger<UpstreamClient>.Instance, new MirrorMetrics());
        var cache = new DiscoveryCache(client, optionsWrapper, NullLogger<DiscoveryCache>.Instance, new MirrorMetrics(), TimeProvider.System);

        DiscoverySnapshot first = await cache.GetAsync(ct);
        Assert.NotNull(first);

        // Force TTL to zero so the cache will attempt refresh on next call
        var zeroTtlOptions = new MirrorOptions { Upstream = { DiscoveryCacheTtl = TimeSpan.Zero } };
        IOptions<MirrorOptions> zeroTtlWrapper = Microsoft.Extensions.Options.Options.Create(zeroTtlOptions);
        var failingHttpClient = new TestHttpClientFactory(new FailingHandler());
        var failingClient = new UpstreamClient(failingHttpClient, zeroTtlWrapper, NullLogger<UpstreamClient>.Instance, new MirrorMetrics());
        var failingCache = new DiscoveryCache(failingClient, zeroTtlWrapper, NullLogger<DiscoveryCache>.Instance, new MirrorMetrics(), TimeProvider.System);

        // Manually seed the failing cache so it has stale data to serve
        FieldInfo? snapshotField = typeof(DiscoveryCache).GetField("_current", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(snapshotField);
        snapshotField.SetValue(failingCache, first);

        // On refresh failure, it should return the stale seed data
        DiscoverySnapshot result = await failingCache.GetAsync(ct);
        Assert.NotNull(result);
        Assert.Equal(first.FetchedAt, result.FetchedAt);
    }

    [Fact]
    public async Task TtlExpiry_TriggersRefresh()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var options = new MirrorOptions
        {
            Upstream = { DiscoveryCacheTtl = TimeSpan.FromMinutes(30) },
            RepositorySignatures = { Enabled = true },
            Cache = { Enabled = true, RepositorySignatures = { Enabled = true } },
        };
        IOptions<MirrorOptions> optionsWrapper = Microsoft.Extensions.Options.Options.Create(options);
        using var handler = new CountingHandler(TestFixtures.NuGetOrgServiceIndex);
        var httpClient = new TestHttpClientFactory(handler);
        var client = new UpstreamClient(httpClient, optionsWrapper, NullLogger<UpstreamClient>.Instance, new MirrorMetrics());
        var cache = new DiscoveryCache(client, optionsWrapper, NullLogger<DiscoveryCache>.Instance, new MirrorMetrics(), clock);

        // First fetch — should call upstream
        DiscoverySnapshot first = await cache.GetAsync(ct);
        Assert.NotNull(first);
        Assert.Equal(1, handler.CallCount);

        // Second fetch within TTL — should use cached snapshot
        DiscoverySnapshot cached = await cache.GetAsync(ct);
        Assert.Same(first, cached);
        Assert.Equal(1, handler.CallCount);

        // Advance past TTL — next fetch should call upstream again
        clock.Advance(TimeSpan.FromMinutes(31));
        DiscoverySnapshot refreshed = await cache.GetAsync(ct);
        Assert.NotSame(first, refreshed);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task GetAsync_FirstDiscoveryFails_NoStaleCache_Throws()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        IOptions<MirrorOptions> optionsWrapper = Microsoft.Extensions.Options.Options.Create(new MirrorOptions());
        var httpClient = new TestHttpClientFactory(new FailingHandler());
        var client = new UpstreamClient(httpClient, optionsWrapper, NullLogger<UpstreamClient>.Instance, new MirrorMetrics());
        var cache = new DiscoveryCache(client, optionsWrapper, NullLogger<DiscoveryCache>.Instance, new MirrorMetrics(), TimeProvider.System);

        await Assert.ThrowsAsync<HttpRequestException>(() => cache.GetAsync(ct));
    }

    [Fact]
    public async Task RefreshAsync_FirstDiscoveryFails_NoStaleCache_Throws()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        IOptions<MirrorOptions> optionsWrapper = Microsoft.Extensions.Options.Options.Create(new MirrorOptions());
        var httpClient = new TestHttpClientFactory(new FailingHandler());
        var client = new UpstreamClient(httpClient, optionsWrapper, NullLogger<UpstreamClient>.Instance, new MirrorMetrics());
        var cache = new DiscoveryCache(client, optionsWrapper, NullLogger<DiscoveryCache>.Instance, new MirrorMetrics(), TimeProvider.System);

        await Assert.ThrowsAsync<HttpRequestException>(() => cache.RefreshAsync(ct));
    }

    [Fact]
    public async Task RepositorySignatures_4_7_0_Only_IsMapped()
    {
        const string Index = """
        {
          "version": "3.0.0",
          "resources": [
            {
              "@id": "https://api.nuget.org/v3-flatcontainer/",
              "@type": "PackageBaseAddress/3.0.0"
            },
            {
              "@id": "https://api.nuget.org/v3-index/repository-signatures/4.7.0/index.json",
              "@type": "RepositorySignatures/4.7.0"
            }
          ]
        }
        """;

        var options = new MirrorOptions
        {
            RepositorySignatures = { Enabled = true },
            Cache = { Enabled = true, RepositorySignatures = { Enabled = true } },
        };
        IOptions<MirrorOptions> optionsWrapper = Microsoft.Extensions.Options.Options.Create(options);
        using var handler = new SuccessHandler(Index);
        var httpClient = new TestHttpClientFactory(handler);
        var client = new UpstreamClient(httpClient, optionsWrapper, NullLogger<UpstreamClient>.Instance, new MirrorMetrics());
        var cache = new DiscoveryCache(client, optionsWrapper, NullLogger<DiscoveryCache>.Instance, new MirrorMetrics(), TimeProvider.System);

        DiscoverySnapshot snapshot = await cache.GetAsync(TestContext.Current.CancellationToken);

        Assert.True(snapshot.ForwardMap.ContainsKey("/v3/repository-signatures/"));
        Assert.Equal("https://api.nuget.org/v3-index/repository-signatures/", snapshot.ForwardMap["/v3/repository-signatures/"]);
    }

    [Fact]
    public async Task RepositorySignatures_MalformedId_NotAdvertised()
    {
        const string Index = """
        {
          "version": "3.0.0",
          "resources": [
            {
              "@id": "https://api.nuget.org/v3-flatcontainer/",
              "@type": "PackageBaseAddress/3.0.0"
            },
            {
              "@id": "https://api.nuget.org/v3-index/repository-signatures/",
              "@type": "RepositorySignatures/5.0.0"
            }
          ]
        }
        """;

        var options = new MirrorOptions
        {
            RepositorySignatures = { Enabled = true },
            Cache = { Enabled = true, RepositorySignatures = { Enabled = true } },
        };
        IOptions<MirrorOptions> optionsWrapper = Microsoft.Extensions.Options.Options.Create(options);
        using var handler = new SuccessHandler(Index);
        var httpClient = new TestHttpClientFactory(handler);
        var client = new UpstreamClient(httpClient, optionsWrapper, NullLogger<UpstreamClient>.Instance, new MirrorMetrics());
        var cache = new DiscoveryCache(client, optionsWrapper, NullLogger<DiscoveryCache>.Instance, new MirrorMetrics(), TimeProvider.System);

        DiscoverySnapshot snapshot = await cache.GetAsync(TestContext.Current.CancellationToken);

        Assert.False(snapshot.ForwardMap.ContainsKey("/v3/repository-signatures/"));
    }

    [Fact]
    public async Task ReadmeUriTemplate_NoTemplateVariable_NotMapped()
    {
        const string Index = """
        {
          "version": "3.0.0",
          "resources": [
            {
              "@id": "https://api.nuget.org/v3-flatcontainer/",
              "@type": "PackageBaseAddress/3.0.0"
            },
            {
              "@id": "https://readme-host.example.com/readme",
              "@type": "ReadmeUriTemplate/6.13.0"
            }
          ]
        }
        """;

        IOptions<MirrorOptions> optionsWrapper = Microsoft.Extensions.Options.Options.Create(new MirrorOptions());
        using var handler = new SuccessHandler(Index);
        var httpClient = new TestHttpClientFactory(handler);
        var client = new UpstreamClient(httpClient, optionsWrapper, NullLogger<UpstreamClient>.Instance, new MirrorMetrics());
        var cache = new DiscoveryCache(client, optionsWrapper, NullLogger<DiscoveryCache>.Instance, new MirrorMetrics(), TimeProvider.System);

        DiscoverySnapshot snapshot = await cache.GetAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(snapshot.RewritePairs, r => string.Equals(r.UpstreamPrefix, "https://readme-host.example.com/readme", StringComparison.Ordinal));
    }

    private static async Task<DiscoverySnapshot> DiscoverFromFixtureAsync(string indexJson)
    {
        var options = new MirrorOptions
        {
            RepositorySignatures = { Enabled = true },
            Cache = { Enabled = true, RepositorySignatures = { Enabled = true } },
        };
        IOptions<MirrorOptions> optionsWrapper = Microsoft.Extensions.Options.Options.Create(options);
        using var handler = new SuccessHandler(indexJson);
        var httpClient = new TestHttpClientFactory(handler);
        var client = new UpstreamClient(httpClient, optionsWrapper, NullLogger<UpstreamClient>.Instance, new MirrorMetrics());
        var cache = new DiscoveryCache(client, optionsWrapper, NullLogger<DiscoveryCache>.Instance, new MirrorMetrics(), TimeProvider.System);

        return await cache.GetAsync(TestContext.Current.CancellationToken);
    }

    private sealed class CountingHandler(string responseBody) : HttpMessageHandler
    {
        private int _callCount;
        public int CallCount => _callCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody),
            });
        }
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
