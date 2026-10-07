using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using NuGetMirror.TestKit;
using NuGetMirror.TestKit.Logger;

namespace NuGetMirror.Contract.Tests;

public sealed class CachingContractTestFactory(ITestOutputHelper testOutputHelper) : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly StubUpstreamHandler _stub = new();

    public async ValueTask InitializeAsync()
    {
        await Task.CompletedTask;
    }

    public new async ValueTask DisposeAsync()
    {
        _stub.Dispose();
        await base.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureLogging(loggingBuilder =>
        {
            loggingBuilder.ClearProviders();
            loggingBuilder.AddProvider(new XunitTestOutputLoggerProvider(testOutputHelper));
        });

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mirror:PublicBaseUrl"] = "https://mirror.test",
                ["Mirror:Upstream:Resilience:Enabled"] = "false",
                ["Mirror:RepositorySignatures:Enabled"] = "true",
                ["Mirror:Cache:Enabled"] = "true",
                ["Mirror:Cache:RepositorySignatures:Enabled"] = "true",
                ["Mirror:Cache:Registration:Enabled"] = "true",
                ["Mirror:Cache:FileSystem:Directory"] = Path.Combine(Path.GetTempPath(), "reg-contract-cache-" + Guid.NewGuid()),
            });
        });

        builder.ConfigureServices(services =>
        {
            string fixturesDir = Path.Join(AppContext.BaseDirectory, "Fixtures", "Upstream");

            _stub
                .MapJson("https://api.nuget.org/v3/index.json",
                    File.ReadAllText(Path.Join(fixturesDir, "index.json")))
                .MapJson("https://api.nuget.org/v3/registration5-gz-semver2/newtonsoft.json/index.json",
                    File.ReadAllText(Path.Join(fixturesDir, "registration-newtonsoft.json")))
                .MapJson("https://api.nuget.org/v3/registration5-gz-semver2/newtonsoft.json/3.5.8.json",
                    File.ReadAllText(Path.Join(fixturesDir, "registration-page-newtonsoft.json")))
                .MapJson("https://api.nuget.org/v3-flatcontainer/newtonsoft.json/index.json",
                    File.ReadAllText(Path.Join(fixturesDir, "flatcontainer-newtonsoft-index.json")));

            services.AddHttpClient("upstream-buffered")
                .ConfigurePrimaryHttpMessageHandler(() => _stub);
            services.AddHttpClient("upstream-stream")
                .ConfigurePrimaryHttpMessageHandler(() => _stub);
        });
    }
}
