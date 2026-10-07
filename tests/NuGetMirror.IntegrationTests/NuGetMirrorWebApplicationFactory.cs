using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using NuGetMirror.TestKit.Logger;

namespace NuGetMirror.IntegrationTests;

internal sealed class NuGetMirrorWebApplicationFactory(
    ITestOutputHelper? testOutputHelper,
    Action<IWebHostBuilder>? configureWebHost = null,
    Action<IServiceCollection>? configureServices = null
    ) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Mirror:Cache:Enabled", "true");
        builder.UseSetting("Mirror:Cache:Readme:Enabled", "true");
        builder.UseSetting("Mirror:Cache:NegativeCache:Enabled", "true");
        builder.UseSetting("Mirror:Upstream:Resilience:Enabled", "false");
        builder.ConfigureLogging(loggingBuilder =>
        {
            loggingBuilder.ClearProviders();
            if (testOutputHelper is not null)
            {
                loggingBuilder.AddProvider(new XunitTestOutputLoggerProvider(testOutputHelper));
            }
        });

        if (configureServices is not null)
        {
            builder.ConfigureServices(configureServices);
        }

        configureWebHost?.Invoke(builder);
    }
}
