using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Reflection;

using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

using NuGetMirror.Configuration;
using NuGetMirror.Diagnostics;
using NuGetMirror.Diagnostics.HealthChecks;
using NuGetMirror.Discovery;
using NuGetMirror.Proxy;
using NuGetMirror.ServiceDefaults;
using NuGetMirror.Storage;
using NuGetMirror.Storage.S3;
using NuGetMirror.Upstream;

namespace NuGetMirror;

[SuppressMessage("Maintainability", "CA1506: Avoid excessive class coupling", Justification = "This is necessary to configure needed services.")]
public sealed partial class Program
{
    private static Task Main(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(args);
        builder.AddServiceDefaults();

        builder.Services.AddOpenTelemetry().WithMetrics(m => m.AddMeter(MirrorMetrics.MeterName));

        builder.Services.AddOptions<MirrorOptions>()
            .Bind(builder.Configuration.GetSection(MirrorOptions.SectionName))
            .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<MirrorOptions>, MirrorOptionsValidator>();
        builder.Services.AddSingleton<IValidateOptions<MirrorOptions>, MirrorOptionsS3Validator>();
        builder.Services.AddSingleton<IValidateOptions<MirrorOptions>, MirrorOptionsBasePathValidator>();
        builder.Services.AddSingleton<IValidateOptions<MirrorOptions>, MirrorOptionsUpstreamValidator>();
        builder.Services.AddSingleton<IValidateOptions<MirrorOptions>, MirrorOptionsCacheValidator>();
        builder.Services.AddSingleton<IValidateOptions<MirrorOptions>, MirrorOptionsResilienceValidator>();
        builder.Services.AddUpstreamResilience();
        builder.Services.AddSingleton<DiscoveryCache>();
        builder.Services.AddHostedService<DiscoveryRefreshService>();
        builder.Services.AddSingleton<KeyedAsyncLock>();
        builder.Services.AddSingleton<Forwarder>();
        builder.Services.AddSingleton<VulnerabilityForwarder>();
        builder.Services.AddSingleton<RepositorySignaturesForwarder>();
        builder.Services.AddSingleton<CacheStatsState>();
        builder.Services.AddSingleton(sp => new MirrorMetrics(sp.GetRequiredService<CacheStatsState>()));
        builder.Services.AddSingleton(sp => new NegativeCache(
            sp.GetRequiredService<IOptions<MirrorOptions>>().Value.Cache.NegativeCache.MaxEntries,
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<MirrorMetrics>()));
        RegisterPackageStore(builder.Services, builder.Configuration.GetSection(MirrorOptions.SectionName));

        builder.Services.AddHealthChecks()
            .AddCheck<UpstreamHealthCheck>("upstream", failureStatus: HealthStatus.Unhealthy, tags: ["ready"], timeout: TimeSpan.FromSeconds(5))
            .AddCheck<StorageHealthCheck>("storage", failureStatus: HealthStatus.Unhealthy, tags: ["ready"], timeout: TimeSpan.FromSeconds(5));

        WebApplication app = builder.Build();

        app.MapDefaultEndpoints();
        app.MapMirror();

        PrintVersion(app.Services.GetRequiredService<ILogger<Program>>());

        return app.RunAsync();
    }

    private static void RegisterPackageStore(IServiceCollection services, IConfiguration configurationSection)
    {
        MirrorOptions mirrorOptions = configurationSection.Get<MirrorOptions>() ?? new MirrorOptions();
        string backend = mirrorOptions.Cache.Backend;

        if (string.Equals(backend, "S3", StringComparison.OrdinalIgnoreCase))
        {
            services.AddHttpClient<S3Client>()
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
                {
                    AutomaticDecompression = DecompressionMethods.All,
                    PooledConnectionLifetime = UpstreamHandlerFactory.s_defaultPooledConnectionLifetime,
                    MaxConnectionsPerServer = UpstreamHandlerFactory.DefaultMaxConnectionsPerServer,
                });
            services.AddSingleton<S3PackageContentStore>();
            services.AddSingleton<IPackageContentStore>(sp => sp.GetRequiredService<S3PackageContentStore>());
            services.AddHostedService<S3CachePreflightService>();
        }
        else
        {
            services.AddSingleton<FileSystemPackageContentStore>();
            services.AddSingleton<IPackageContentStore>(sp => sp.GetRequiredService<FileSystemPackageContentStore>());
            services.AddHostedService<FileSystemCachePreflightService>();
        }

        services.AddSingleton(TimeProvider.System);
        services.AddHostedService<CacheEvictionService>();
    }

    private static void PrintVersion(ILogger logger)
    {
        Assembly assembly = typeof(Program).Assembly;
        string? assemblyVersion = assembly
            .GetCustomAttributes<AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?
            .InformationalVersion;

        LogApplicationStarted(logger, assemblyVersion ?? string.Empty);
    }

    [LoggerMessage(LogLevel.Information, "NuGetMirror started. Version: {version}")]
    private static partial void LogApplicationStarted(ILogger logger, string version);
}
