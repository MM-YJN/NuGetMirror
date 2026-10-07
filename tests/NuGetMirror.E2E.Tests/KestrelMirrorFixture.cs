using DotNet.Testcontainers.Configurations;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using NuGetMirror.Configuration;
using NuGetMirror.TestKit;
using NuGetMirror.TestKit.Logger;

namespace NuGetMirror.E2E.Tests;

public sealed class KestrelMirrorFixture : IAsyncLifetime
{
    /// <summary>
    /// The <c>BasePath</c> used by the secondary mirror server started by this fixture.
    /// Tests that need a prefixed feed (e.g. <c>/nuget/v3/index.json</c>) should use
    /// <see cref="BasePathMirrorPort"/> together with this constant.
    /// </summary>
    public const string BasePath = "/nuget";

    private readonly StubUpstreamHandler _stub = new();
    private WebApplicationFactory<Program> _factory = null!;
    private WebApplicationFactory<Program> _basePathFactory = null!;

    public string MirrorAddress { get; private set; } = null!;

    public int MirrorPort { get; private set; }

    public string BasePathMirrorAddress { get; private set; } = null!;

    public int BasePathMirrorPort { get; private set; }

    public string CacheDirectory { get; } = Path.Join(Path.GetTempPath(), "nuget-mirror-e2e-cache-" + Guid.NewGuid().ToString("N"));

    public string BasePathCacheDirectory { get; } = Path.Join(Path.GetTempPath(), "nuget-mirror-e2e-bp-cache-" + Guid.NewGuid().ToString("N"));

    public int GetUpstreamCount(string url) => _stub.GetCount(url);

    public int GetUpstreamSearchCount() => _stub.GetCountByPrefix("https://azuresearch.nuget.org/query");

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(CacheDirectory);
        Directory.CreateDirectory(BasePathCacheDirectory);

        byte[] sampleNupkg = SamplePackageFactory.Create();
        byte[] sampleNupkg200 = SamplePackageFactory.Create(SamplePackageFactory.Version2);

        string serviceIndex = /* lang=json */ """
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
            },
            {
              "@id": "https://api.nuget.org/v3/registration5-gz-semver1/",
              "@type": "RegistrationsBaseUrl/3.4.0"
            },
            {
              "@id": "https://api.nuget.org/v3/registration5/",
              "@type": "RegistrationsBaseUrl"
            },
            {
              "@id": "https://azuresearch.nuget.org/query",
              "@type": "SearchQueryService"
            },
            {
              "@id": "https://azuresearch.nuget.org/query",
              "@type": "SearchQueryService/3.0.0-beta"
            },
            {
              "@id": "https://azuresearch.nuget.org/query",
              "@type": "SearchQueryService/3.0.0-rc"
            },
            {
              "@id": "https://azuresearch.nuget.org/query",
              "@type": "SearchQueryService/3.5.0"
            },
            {
              "@id": "https://azuresearch.nuget.org/autocomplete",
              "@type": "SearchAutocompleteService"
            },
            {
              "@id": "https://azuresearch.nuget.org/autocomplete",
              "@type": "SearchAutocompleteService/3.0.0-beta"
            },
            {
              "@id": "https://azuresearch.nuget.org/autocomplete",
              "@type": "SearchAutocompleteService/3.0.0-rc"
            },
            {
              "@id": "https://azuresearch.nuget.org/autocomplete",
              "@type": "SearchAutocompleteService/3.5.0"
            },
            {
              "@id": "https://api.nuget.org/v3/vulnerabilities/index.json",
              "@type": "VulnerabilityInfo/6.7.0"
            }
          ]
        }
        """;

        string registration = /* lang=json */ $$"""
        {
          "@id": "https://api.nuget.org/v3/registration5-gz-semver2/testmirror.sample/index.json",
          "@type": ["catalog:CatalogRoot", "PackageRegistration", "catalog:Permalink"],
          "commitId": "00000000-0000-0000-0000-000000000001",
          "commitTimeStamp": "2025-01-01T00:00:00.0000000Z",
          "count": 1,
          "items": [
            {
              "@id": "https://api.nuget.org/v3/registration5-gz-semver2/testmirror.sample/index.json#page/1.0.0/2.0.0",
              "@type": "catalog:CatalogPage",
              "commitId": "00000000-0000-0000-0000-000000000001",
              "commitTimeStamp": "2025-01-01T00:00:00.0000000Z",
              "count": 2,
              "lower": "1.0.0",
              "upper": "2.0.0",
              "items": [
                {
                  "@id": "https://api.nuget.org/v3/registration5-gz-semver2/testmirror.sample/1.0.0.json",
                  "@type": "Package",
                  "catalogEntry": {
                    "@id": "https://api.nuget.org/v3/catalog0/data/2025.01.01.00.00.00/testmirror.sample.1.0.0.json",
                    "@type": "PackageDetails",
                    "id": "testmirror.sample",
                    "version": "1.0.0",
                    "listed": true,
                    "packageContent": "https://api.nuget.org/v3-flatcontainer/testmirror.sample/1.0.0/testmirror.sample.1.0.0.nupkg"
                  },
                  "packageContent": "https://api.nuget.org/v3-flatcontainer/testmirror.sample/1.0.0/testmirror.sample.1.0.0.nupkg"
                },
                {
                  "@id": "https://api.nuget.org/v3/registration5-gz-semver2/testmirror.sample/2.0.0.json",
                  "@type": "Package",
                  "catalogEntry": {
                    "@id": "https://api.nuget.org/v3/catalog0/data/2025.01.01.00.00.00/testmirror.sample.2.0.0.json",
                    "@type": "PackageDetails",
                    "id": "testmirror.sample",
                    "version": "2.0.0",
                    "listed": true,
                    "packageContent": "https://api.nuget.org/v3-flatcontainer/testmirror.sample/2.0.0/testmirror.sample.2.0.0.nupkg"
                  },
                  "packageContent": "https://api.nuget.org/v3-flatcontainer/testmirror.sample/2.0.0/testmirror.sample.2.0.0.nupkg"
                }
              ]
            }
          ]
        }
        """;

        string flatContainerIndex = /* lang=json */ """
        {"versions":["1.0.0","2.0.0"]}
        """;

        string searchResponse = /* lang=json */ """
        {
          "@context": {
            "@vocab": "http://schema.nuget.org/schema#"
          },
          "totalHits": 1,
          "data": [
            {
              "@id": "https://api.nuget.org/v3/registration5-gz-semver2/testmirror.sample/index.json",
              "@type": "Package",
              "registration": "https://api.nuget.org/v3/registration5-gz-semver2/testmirror.sample/index.json",
              "id": "testmirror.sample",
              "version": "1.0.0",
              "description": "E2E test package",
              "title": "testmirror.sample",
              "iconUrl": "https://api.nuget.org/v3-flatcontainer/testmirror.sample/1.0.0/icon",
              "authors": ["TestMirror"],
              "totalDownloads": 0,
              "verified": false,
              "packageTypes": [{ "name": "Dependency" }],
              "versions": [
                {
                  "version": "1.0.0",
                  "downloads": 0,
                  "@id": "https://api.nuget.org/v3/registration5-gz-semver2/testmirror.sample/1.0.0.json"
                }
              ]
            }
          ]
        }
        """;

        string vulnIndex = /* lang=json */ """
        [
            {
                "@name": "base",
                "@id": "https://api.nuget.org/v3-vulnerabilities/2026.06/vulnerability.base.json",
                "@updated": "2026-06-01T00:00:00Z",
                "comment": "E2E test vulnerability data"
            }
        ]
        """;

        string vulnPage = /* lang=json */ """
        {
            "testmirror.sample": [
                {
                    "url": "https://cve.contoso.com/advisories/1",
                    "severity": 2,
                    "versions": "(, 2.0.0)"
                }
            ]
        }
        """;

        _stub
            .MapJson("https://api.nuget.org/v3/index.json", serviceIndex)
            .MapJson("https://api.nuget.org/v3/registration5-gz-semver2/testmirror.sample/index.json", registration)
            .MapJson("https://api.nuget.org/v3-flatcontainer/testmirror.sample/index.json", flatContainerIndex)
            .MapBytes(
                "https://api.nuget.org/v3-flatcontainer/testmirror.sample/1.0.0/testmirror.sample.1.0.0.nupkg",
                sampleNupkg,
                "application/octet-stream")
            .MapBytes(
                "https://api.nuget.org/v3-flatcontainer/testmirror.sample/2.0.0/testmirror.sample.2.0.0.nupkg",
                sampleNupkg200,
                "application/octet-stream")
            .MapJsonPrefix("https://azuresearch.nuget.org/query", searchResponse)
            .MapJson("https://api.nuget.org/v3/vulnerabilities/index.json", vulnIndex)
            .MapJson("https://api.nuget.org/v3-vulnerabilities/2026.06/vulnerability.base.json", vulnPage);

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureLogging(loggingBuilder =>
                {
                    loggingBuilder.ClearProviders();
                    loggingBuilder.AddProvider(new XunitDiagnosticMessageLoggerProvider());
                });

                // Clear ASPNETCORE_HTTP_PORTS / ASPNETCORE_HTTPS_PORTS from configuration so that
                // KestrelServerOptionsSetup does not register code-backed listen options for port
                // 8080 (or any other port from the environment). Without this, UseKestrel(0)
                // cannot override the port because the environment-sourced code-backed options
                // bypass IServerAddressesFeature entirely.
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        { "HTTP_PORTS", "" },
                        { "HTTPS_PORTS", "" },
                    });
                });

                builder.ConfigureServices(services =>
                {
                    services.AddHttpClient("upstream-buffered")
                        .ConfigurePrimaryHttpMessageHandler(() => _stub);
                    services.AddHttpClient("upstream-stream")
                        .ConfigurePrimaryHttpMessageHandler(() => _stub);

                    services.Configure<MirrorOptions>(options =>
                    {
                        options.Cache.FileSystem.Directory = CacheDirectory;
                        options.Cache.Enabled = true;
                        options.Cache.Vulnerability.Enabled = true;
                        options.Cache.Readme.Enabled = true;
                        options.Cache.NegativeCache.Enabled = true;
                        options.Upstream.Resilience.Enabled = false;
                    });
                });
            });

        _factory.UseKestrel(options => options.ListenAnyIP(0));
        _factory.StartServer();

        IServer server = _factory.Services.GetRequiredService<IServer>();
        MirrorAddress = (server.Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("IServerAddressesFeature is not available.")).Addresses.First();
        MirrorPort = new Uri(MirrorAddress).Port;

        _basePathFactory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureLogging(loggingBuilder =>
                {
                    loggingBuilder.ClearProviders();
                    loggingBuilder.AddProvider(new XunitDiagnosticMessageLoggerProvider());
                });

                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        { "HTTP_PORTS", "" },
                        { "HTTPS_PORTS", "" },
                    });
                });

                builder.ConfigureServices(services =>
                {
                    services.AddHttpClient("upstream-buffered")
                        .ConfigurePrimaryHttpMessageHandler(() => _stub);
                    services.AddHttpClient("upstream-stream")
                        .ConfigurePrimaryHttpMessageHandler(() => _stub);

                    services.Configure<MirrorOptions>(options =>
                    {
                        options.BasePath = BasePath;
                        options.Cache.FileSystem.Directory = BasePathCacheDirectory;
                        options.Cache.Enabled = true;
                        options.Cache.Vulnerability.Enabled = true;
                        options.Cache.Readme.Enabled = true;
                        options.Cache.NegativeCache.Enabled = true;
                        options.Upstream.Resilience.Enabled = false;
                    });
                });
            });

        _basePathFactory.UseKestrel(options => options.ListenAnyIP(0));
        _basePathFactory.StartServer();

        IServer basePathServer = _basePathFactory.Services.GetRequiredService<IServer>();
        BasePathMirrorAddress = (basePathServer.Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("IServerAddressesFeature is not available.")).Addresses.First();
        BasePathMirrorPort = new Uri(BasePathMirrorAddress).Port;

        await TestcontainersSettings.ExposeHostPortsAsync(
            [(ushort)MirrorPort, (ushort)BasePathMirrorPort]);
    }

    public async ValueTask DisposeAsync()
    {
        _stub.Dispose();
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        if (_basePathFactory is not null)
        {
            await _basePathFactory.DisposeAsync();
        }

        try
        {
            if (Directory.Exists(CacheDirectory))
            {
                Directory.Delete(CacheDirectory, recursive: true);
            }
        }
        catch
        {
        }

        try
        {
            if (Directory.Exists(BasePathCacheDirectory))
            {
                Directory.Delete(BasePathCacheDirectory, recursive: true);
            }
        }
        catch
        {
        }
    }
}
