using System.Diagnostics;
using System.Reflection;

using Microsoft.Extensions.Options;

using NuGetMirror.Configuration;
using NuGetMirror.Diagnostics;
using NuGetMirror.Discovery;
using NuGetMirror.Json;
using NuGetMirror.Storage;

namespace NuGetMirror.Proxy;

public static partial class MirrorEndpoints
{
    public static IEndpointRouteBuilder MapMirror(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        MirrorOptions mirrorOptions = endpoints.ServiceProvider.GetRequiredService<IOptions<MirrorOptions>>().Value;
        string basePath = mirrorOptions.NormalizedBasePath;

        // When a base path is configured (e.g. "/nuget") all NuGet protocol and admin endpoints
        // are grouped under it. Health endpoints are registered separately (in MapDefaultEndpoints)
        // and intentionally remain at the root for orchestrator/liveness probes.
        IEndpointRouteBuilder target = string.IsNullOrEmpty(basePath)
            ? endpoints
            : (IEndpointRouteBuilder)endpoints.MapGroup(basePath);

        target.MapGet("/v3/index.json", IndexHandlerAsync);
        target.MapMethods("/v3-flatcontainer/{*path}", ["GET", "HEAD"], FlatContainerHandlerAsync);
        target.MapGet("/v3/registration-semver2/{*path}", RegSemver2HandlerAsync);
        target.MapGet("/v3/registration-gz-semver1/{*path}", RegGzSemver1HandlerAsync);
        target.MapGet("/v3/registration-semver1/{*path}", RegSemver1HandlerAsync);
        target.MapGet("/v3/search", SearchHandlerAsync);
        target.MapGet("/v3/autocomplete", AutocompleteHandlerAsync);
        target.MapGet("/v3/catalog0/{*path}", CatalogHandlerAsync);
        target.MapGet("/v3/vulnerability/index.json", VulnerabilityIndexHandlerAsync);
        target.MapGet("/v3/vulnerability-page/{*path}", VulnerabilityPageHandlerAsync);
        target.MapGet("/v3/repository-signatures/{version}/index.json", RepoSignIndexHandlerAsync);
        target.MapMethods("/v3/repository-signatures/certificates/{*path}", ["GET", "HEAD"], RepoSignCertHandlerAsync);

        AdminOptions adminOptions = mirrorOptions.Admin;
        if (adminOptions.Enabled)
        {
            target.MapGet(adminOptions.Path, AdminStatsHandlerAsync);
        }

        return endpoints;
    }

    private static async Task<IResult> IndexHandlerAsync(DiscoveryCache cache, IOptions<MirrorOptions> options, HttpContext context, ILoggerFactory loggerFactory)
    {
        ILogger logger = loggerFactory.CreateLogger("NuGetMirror.MirrorEndpoints");
        CancellationToken ct = context.RequestAborted;
        DiscoverySnapshot snapshot;

        try
        {
            snapshot = await cache.GetAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogFailedToDiscoverUpstreamIndex(logger, ex);
            return Results.Problem(
                detail: ex.Message,
                statusCode: StatusCodes.Status502BadGateway);
        }

        string mirrorBase = ProxyHttp.ResolveMirrorBase(context, options.Value.PublicBaseUrl, options.Value.NormalizedBasePath);
        string body = UrlRewriter.Rewrite(snapshot.RawUpstreamIndexJson, snapshot.RewritePairs, mirrorBase, snapshot.GetOrBuildRewriteTargets(mirrorBase));
        return Results.Text(body, "application/json");
    }

    private static Task FlatContainerHandlerAsync(HttpContext context, Forwarder forwarder, string? path)
    {
        if (PackageCacheKey.IsReadmePath(path))
        {
            return forwarder.ReadmeProxyAsync(context, "/v3-flatcontainer/", path);
        }

        return forwarder.StreamProxyAsync(context, "/v3-flatcontainer/", path);
    }

    private static Task RegSemver2HandlerAsync(HttpContext context, Forwarder forwarder, string? path)
        => forwarder.RegistrationProxyAsync(context, "/v3/registration-semver2/", "semver2", path);

    private static Task RegGzSemver1HandlerAsync(HttpContext context, Forwarder forwarder, string? path)
        => forwarder.RegistrationProxyAsync(context, "/v3/registration-gz-semver1/", "gz-semver1", path);

    private static Task RegSemver1HandlerAsync(HttpContext context, Forwarder forwarder, string? path)
        => forwarder.RegistrationProxyAsync(context, "/v3/registration-semver1/", "semver1", path);

    private static Task SearchHandlerAsync(HttpContext context, Forwarder forwarder)
        => forwarder.RewriteProxyAsync(context, "/v3/search", null);

    private static Task AutocompleteHandlerAsync(HttpContext context, Forwarder forwarder)
        => forwarder.RewriteProxyAsync(context, "/v3/autocomplete", null);

    private static Task CatalogHandlerAsync(HttpContext context, Forwarder forwarder, string? path)
        => forwarder.RewriteProxyAsync(context, "/v3/catalog0/", path);

    private static Task VulnerabilityIndexHandlerAsync(HttpContext context, VulnerabilityForwarder forwarder)
        => forwarder.IndexHandlerAsync(context);

    private static Task VulnerabilityPageHandlerAsync(HttpContext context, VulnerabilityForwarder forwarder, string path)
        => forwarder.PageHandlerAsync(context, path);

    private static Task RepoSignIndexHandlerAsync(HttpContext context, RepositorySignaturesForwarder forwarder, string version)
        => forwarder.IndexHandlerAsync(context, version);

    private static Task RepoSignCertHandlerAsync(HttpContext context, RepositorySignaturesForwarder forwarder, string? path)
        => forwarder.CertificateHandlerAsync(context, path ?? string.Empty);

    [LoggerMessage(LogLevel.Error, "Failed to discover upstream index.")]
    private static partial void LogFailedToDiscoverUpstreamIndex(ILogger logger, Exception ex);

    [LoggerMessage(LogLevel.Warning, "Discovery snapshot not yet available.")]
    private static partial void LogDiscoverySnapshotNotAvailable(ILogger logger, Exception ex);

    private static async Task<IResult> AdminStatsHandlerAsync(
        DiscoveryCache discovery,
        IOptions<MirrorOptions> options,
        CacheStatsState cacheStats,
        IPackageContentStore? store,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider,
        NegativeCache negativeCache)
    {
        ILogger logger = loggerFactory.CreateLogger("NuGetMirror.MirrorEndpoints.AdminStats");

        UpstreamOptions upstreamOptions = options.Value.Upstream;
        CacheOptions cacheOptions = options.Value.Cache;
        DiscoverySnapshot? snapshot;

        try
        {
            snapshot = discovery.CurrentSnapshot;
        }
        catch (InvalidOperationException ex)
        {
            LogDiscoverySnapshotNotAvailable(logger, ex);
            snapshot = null;
        }

        bool? storageHealthy = null;
        string? storageError = null;

        if (store is IStorageHealthProbe probe)
        {
            try
            {
                await probe.CheckAsync(CancellationToken.None).ConfigureAwait(false);
                storageHealthy = true;
            }
            catch (Exception ex)
            {
                storageHealthy = false;
                storageError = ex.Message;
            }
        }
        else if (store is not null)
        {
            storageHealthy = true;
        }

        string version = Assembly.GetEntryAssembly()?.GetName()?.Version?.ToString() ?? "0.0.0";
        TimeSpan uptime;
        using (var process = Process.GetCurrentProcess())
        {
            uptime = timeProvider.GetUtcNow() - process.StartTime.ToUniversalTime();
        }
        DateTimeOffset lastUpdate = cacheStats.LastUpdateUtc;

        var response = new MirrorStatsResponse
        {
            Version = version,
            Uptime = uptime.ToString(),
            Upstream = new UpstreamStats
            {
                IndexUrl = upstreamOptions.IndexUrl,
                DiscoveredAt = snapshot?.FetchedAt.ToString("O"),
                ResourceCount = snapshot?.ForwardMap.Count ?? 0,
            },
            Cache = new CacheStats
            {
                Enabled = cacheOptions.Enabled,
                Backend = cacheOptions.Backend,
                EvictionEnabled = cacheOptions.Eviction.Enabled,
                NegativeCacheEnabled = cacheOptions.NegativeCache.Enabled,
                NegativeCacheEntries = negativeCache.Count,
                SizeBytes = cacheStats.SizeBytes,
                EntryCount = cacheStats.EntryCount,
                LastUpdatedUtc = lastUpdate == DateTimeOffset.MinValue ? null : lastUpdate.ToString("O"),
            },
            Storage = new StorageStats
            {
                Healthy = storageHealthy,
                Error = storageError,
            },
        };

        return Results.Json(response, MirrorJsonContext.Default.MirrorStatsResponse, "application/json", StatusCodes.Status200OK);
    }
}
