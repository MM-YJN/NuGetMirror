using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NuGetMirror.Configuration;
using NuGetMirror.Diagnostics;
using NuGetMirror.Discovery;
using NuGetMirror.TestKit;
using NuGetMirror.Upstream;

namespace NuGetMirror.UnitTests;

public sealed class DiscoveryCacheExtraRewriteHostsTests
{
    private const string BasicIndex = """
    {
      "version": "3.0.0",
      "resources": [
        {
          "@id": "https://api.nuget.org/v3-flatcontainer/",
          "@type": "PackageBaseAddress/3.0.0"
        },
        {
          "@id": "https://api.nuget.org/v3/registration5-gz-semver2/",
          "@type": "RegistrationsBaseUrl/3.6.0"
        }
      ]
    }
    """;

    [Fact]
    public async Task ExtraRewriteHosts_AddsRewritePairs()
    {
        DiscoverySnapshot snapshot = await DiscoverWithExtraHostsAsync(["https://third-party.example.com/"]);

        Assert.Contains(snapshot.RewritePairs, r => r.UpstreamPrefix == "https://third-party.example.com/" && r.MirrorPrefix == "/v3/registration-semver2/");
    }

    [Fact]
    public async Task ExtraRewriteHosts_NormalizesMissingTrailingSlash()
    {
        DiscoverySnapshot snapshot = await DiscoverWithExtraHostsAsync(["https://third-party.example.com"]);

        Assert.Contains(snapshot.RewritePairs, r => r.UpstreamPrefix == "https://third-party.example.com/" && r.MirrorPrefix == "/v3/registration-semver2/");
    }

    [Fact]
    public async Task ExtraRewriteHosts_MultipleHosts_AllAdded()
    {
        DiscoverySnapshot snapshot = await DiscoverWithExtraHostsAsync([
            "https://corp-a.example.com/",
            "https://corp-b.example.com/",
        ]);

        Assert.Contains(snapshot.RewritePairs, r => r.UpstreamPrefix == "https://corp-a.example.com/");
        Assert.Contains(snapshot.RewritePairs, r => r.UpstreamPrefix == "https://corp-b.example.com/");
    }

    [Fact]
    public async Task ExtraRewriteHosts_EmptyList_NoExtraPairs()
    {
        DiscoverySnapshot snapshot = await DiscoverWithExtraHostsAsync([]);

        Assert.DoesNotContain(snapshot.RewritePairs, r => r.MirrorPrefix == "/v3/registration-semver2/" && !r.UpstreamPrefix.StartsWith("https://api.nuget.org", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExtraRewriteHosts_RewritesUrls_FromExtraHost()
    {
        string extraHost = "https://external-registry.example.com/";
        DiscoverySnapshot snapshot = await DiscoverWithExtraHostsAsync([extraHost]);

        string json = """
        {
          "items": [{
            "registration": "https://external-registry.example.com/v3/registration5-gz-semver2/newtonsoft.json/index.json"
          }]
        }
        """;

        string rewritten = UrlRewriter.Rewrite(json, snapshot.RewritePairs, "http://localhost:5049");

        Assert.Contains("http://localhost:5049/v3/registration-semver2/", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("external-registry.example.com", rewritten, StringComparison.Ordinal);
    }

    private static async Task<DiscoverySnapshot> DiscoverWithExtraHostsAsync(List<string> extraRewriteHosts)
    {
        var options = new MirrorOptions
        {
            Upstream = { ExtraRewriteHosts = extraRewriteHosts },
        };

        IOptions<MirrorOptions> optionsWrapper = Microsoft.Extensions.Options.Options.Create(options);
        using var handler = new StubUpstreamHandler();
        handler.MapJson("https://api.nuget.org/v3/index.json", BasicIndex);

        var httpClient = new TestHttpClientFactory(handler);
        var client = new UpstreamClient(httpClient, optionsWrapper, NullLogger<UpstreamClient>.Instance, new MirrorMetrics());
        var cache = new DiscoveryCache(client, optionsWrapper, NullLogger<DiscoveryCache>.Instance, new MirrorMetrics(), TimeProvider.System);

        return await cache.GetAsync(TestContext.Current.CancellationToken);
    }
}
