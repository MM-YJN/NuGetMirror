using System.Diagnostics;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Options;

using NuGetMirror.Configuration;
using NuGetMirror.Diagnostics;
using NuGetMirror.Json;
using NuGetMirror.Upstream;

namespace NuGetMirror.Discovery;

internal sealed partial class DiscoveryCache(
    UpstreamClient client,
    IOptions<MirrorOptions> options,
    ILogger<DiscoveryCache> logger,
    MirrorMetrics metrics,
    TimeProvider timeProvider
    ) : IDisposable
{
    private static readonly (string[] Types, string RoutePrefix, string MirrorPrefix)[] s_canonicalMappings =
    [
        (["PackageBaseAddress/3.0.0"], "/v3-flatcontainer/", "/v3-flatcontainer/"),
        (["RegistrationsBaseUrl/3.6.0"], "/v3/registration-semver2/", "/v3/registration-semver2/"),
        (["RegistrationsBaseUrl/3.4.0"], "/v3/registration-gz-semver1/", "/v3/registration-gz-semver1/"),
        (["RegistrationsBaseUrl", "RegistrationsBaseUrl/3.0.0-rc", "RegistrationsBaseUrl/3.0.0-beta"], "/v3/registration-semver1/", "/v3/registration-semver1/"),
    ];

    // Endpoint-style services are exact URLs (not base URLs) and are listed multiple
    // times in the service index (one entry per host and per protocol version). All
    // matching entries share the same mirror prefix so every upstream host @id in the
    // index is rewritten to the mirror, while the first one is used for forwarding.
    private static readonly (string TypeBase, string MirrorPrefix)[] s_endpointMappings =
    [
        ("SearchQueryService", "/v3/search"),
        ("SearchAutocompleteService", "/v3/autocomplete"),
    ];

    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile DiscoverySnapshot? _current;

    public async Task<DiscoverySnapshot> GetAsync(CancellationToken ct)
    {
        DiscoverySnapshot? current = _current;
        TimeSpan ttl = options.Value.Upstream.DiscoveryCacheTtl;

        if (current is not null && timeProvider.GetUtcNow() - current.FetchedAt < ttl)
        {
            TimeSpan age = timeProvider.GetUtcNow() - current.FetchedAt;
            LogCachedDiscoveryUsed(age.ToString());
            return current;
        }

        await _lock.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            current = _current;

            if (current is not null && timeProvider.GetUtcNow() - current.FetchedAt < ttl)
            {
                TimeSpan age = timeProvider.GetUtcNow() - current.FetchedAt;
                LogCachedDiscoveryUsed(age.ToString());
                return current;
            }

            DiscoverySnapshot snapshot = await DiscoverAsync(ct).ConfigureAwait(false);
            _current = snapshot;
            return snapshot;
        }
        catch (Exception ex)
            when (ex is not OperationCanceledException)
        {
            LogFailedToRefreshDiscoverySnapshot(ex);

            if (_current is not null)
            {
                metrics.RecordDiscoveryStaleFallback();
                return _current;
            }

            metrics.RecordDiscoveryFailed();
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    public DiscoverySnapshot CurrentSnapshot
        => _current ?? throw new InvalidOperationException("Discovery has not been performed yet.");

    public async Task RefreshAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            DiscoverySnapshot snapshot = await DiscoverAsync(ct).ConfigureAwait(false);
            _current = snapshot;
        }
        catch (Exception ex)
            when (ex is not OperationCanceledException)
        {
            LogFailedToRefreshDiscoverySnapshot(ex);

            if (_current is not null)
            {
                metrics.RecordDiscoveryStaleFallback();
            }
            else
            {
                metrics.RecordDiscoveryFailed();
                throw;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<DiscoverySnapshot> DiscoverAsync(CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();
        (ServiceIndex? index, string? rawJson) = await FetchServiceIndexAsync(ct).ConfigureAwait(false);

        List<ServiceResource> resources = index.Resources;
        var forwardMap = new Dictionary<string, string>();
        var rewritePairs = new List<RewritePair>();

        MapCanonicalServices(resources, forwardMap, rewritePairs);

        foreach ((string? typeBase, string? mirrorPrefix) in s_endpointMappings)
        {
            MapEndpointService(resources, typeBase, mirrorPrefix, forwardMap, rewritePairs);
        }

        MapCatalogService(resources, forwardMap, rewritePairs);
        MapRepositorySignaturesService(resources, forwardMap, rewritePairs);
        MapVulnerabilityService(resources, forwardMap, rewritePairs);
        MapReadmeUriTemplateService(resources, rewritePairs);
        AppendExtraRewriteHosts(options.Value.Upstream.ExtraRewriteHosts, rewritePairs);

        var snapshot = new DiscoverySnapshot(
            timeProvider.GetUtcNow(),
            rawJson,
            forwardMap,
            rewritePairs);
        LogDiscoveryRefreshed(forwardMap.Count);
        metrics.RecordDiscoveryRefresh(Stopwatch.GetElapsedTime(started).TotalSeconds);
        return snapshot;
    }

    private void MapEndpointService(
        List<ServiceResource> resources,
        string typeBase,
        string mirrorPrefix,
        Dictionary<string, string> forwardMap,
        List<RewritePair> rewritePairs)
    {
        string versionedPrefix = typeBase + "/";
        var ids = new List<string>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < resources.Count; i++)
        {
            string type = resources[i].Type;

            if (!string.Equals(type, typeBase, StringComparison.Ordinal)
                && !type.StartsWith(versionedPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            string id = resources[i].Id;

            if (seenIds.Add(id))
            {
                ids.Add(id);
            }
        }

        if (ids.Count == 0)
        {
            LogMissingUpstreamServiceIndex(typeBase);
            return;
        }

        // Forward to the first (primary) upstream endpoint; rewrite every host so no
        // secondary endpoint @id leaks through the rewritten service index.
        forwardMap[mirrorPrefix] = ids[0];

        foreach (string id in ids)
        {
            rewritePairs.Add(new RewritePair(id, mirrorPrefix));
        }
    }

    private void MapCatalogService(
        List<ServiceResource> resources,
        Dictionary<string, string> forwardMap,
        List<RewritePair> rewritePairs)
    {
        const string CatalogType = "Catalog/3.0.0";
        const string CatalogMirrorPrefix = "/v3/catalog0/";

        for (int i = 0; i < resources.Count; i++)
        {
            if (!string.Equals(resources[i].Type, CatalogType, StringComparison.Ordinal))
            {
                continue;
            }

            string id = resources[i].Id;
            int lastSlash = id.LastIndexOf('/');
            string baseUrl = lastSlash >= 0 ? id[..(lastSlash + 1)] : NormalizeBaseUrl(id);

            forwardMap[CatalogMirrorPrefix] = baseUrl;
            rewritePairs.Add(new RewritePair(baseUrl, CatalogMirrorPrefix));
            return;
        }

        LogCatalogNotAdvertised();
    }

    private void MapRepositorySignaturesService(
        List<ServiceResource> resources,
        Dictionary<string, string> forwardMap,
        List<RewritePair> rewritePairs)
    {
        if (!options.Value.RepositorySignatures.Enabled
            || !options.Value.Cache.RepositorySignatures.Enabled
            || !options.Value.Cache.Enabled)
        {
            return;
        }

        const string RepoSigMirrorPrefix = "/v3/repository-signatures/";

        string[] versionedTypes = new[] { "RepositorySignatures/5.0.0", "RepositorySignatures/4.7.0" };
        ServiceResource? found = TryFindResourceByType(resources, versionedTypes);

        if (found is null)
        {
            LogRepositorySignaturesNotAdvertised();
            return;
        }

        string id = found.Id;
        string versionIndexSuffix = $"/{found.Type[(found.Type.LastIndexOf('/') + 1)..]}/index.json";

        if (!id.EndsWith(versionIndexSuffix, StringComparison.Ordinal))
        {
            LogRepositorySignaturesNotAdvertised();
            return;
        }

        string baseUrl = id[..^versionIndexSuffix.Length];
        baseUrl = NormalizeBaseUrl(baseUrl);

        forwardMap[RepoSigMirrorPrefix] = baseUrl;
        rewritePairs.Add(new RewritePair(baseUrl, RepoSigMirrorPrefix));
    }

    private void MapVulnerabilityService(
        List<ServiceResource> resources,
        Dictionary<string, string> forwardMap,
        List<RewritePair> rewritePairs)
    {
        const string VulnerabilityTypeBase = "VulnerabilityInfo";
        const string VulnerabilityMirrorPrefix = "/v3/vulnerability/";
        const string VulnerabilityIndexPath = "/v3/vulnerability/index.json";

        for (int i = 0; i < resources.Count; i++)
        {
            string type = resources[i].Type;

            if (!string.Equals(type, VulnerabilityTypeBase, StringComparison.Ordinal)
                && !type.StartsWith(VulnerabilityTypeBase + "/", StringComparison.Ordinal))
            {
                continue;
            }

            string id = resources[i].Id;
            int lastSlash = id.LastIndexOf('/');
            string baseUrl = lastSlash >= 0 ? id[..(lastSlash + 1)] : NormalizeBaseUrl(id);

            forwardMap[VulnerabilityMirrorPrefix] = baseUrl;
            rewritePairs.Add(new RewritePair(id, VulnerabilityIndexPath));
            return;
        }

        LogVulnerabilityNotAdvertised();
    }

    private void MapReadmeUriTemplateService(
        List<ServiceResource> resources,
        List<RewritePair> rewritePairs)
    {
        const string ReadmeUriTemplateTypeBase = "ReadmeUriTemplate";
        const string FlatContainerMirrorPrefix = "/v3-flatcontainer/";

        for (int i = 0; i < resources.Count; i++)
        {
            string type = resources[i].Type;

            if (!string.Equals(type, ReadmeUriTemplateTypeBase, StringComparison.Ordinal)
                && !type.StartsWith(ReadmeUriTemplateTypeBase + "/", StringComparison.Ordinal))
            {
                continue;
            }

            string id = resources[i].Id;
            int templateStart = id.IndexOf('{', StringComparison.Ordinal);

            if (templateStart <= 0)
            {
                // No template variables — nothing useful to extract.
                return;
            }

            string baseUrl = id[..templateStart];

            // Only add a rewrite pair if this base URL is not already covered by
            // the flat-container canonical mapping (e.g. same CDN host).
            foreach (RewritePair existing in rewritePairs)
            {
                if (string.Equals(existing.UpstreamPrefix, baseUrl, StringComparison.Ordinal))
                {
                    return;
                }
            }

            rewritePairs.Add(new RewritePair(baseUrl, FlatContainerMirrorPrefix));
            return;
        }

        LogReadmeUriTemplateNotAdvertised();
    }

    private void MapCanonicalServices(
        List<ServiceResource> resources,
        Dictionary<string, string> forwardMap,
        List<RewritePair> rewritePairs)
    {
        var seenMirrorPrefixes = new HashSet<string>();

        foreach ((string[]? types, string? routePrefix, string? mirrorPrefix) in s_canonicalMappings)
        {
            ServiceResource? found = TryFindResourceByType(resources, types);

            if (found is null)
            {
                LogMissingUpstreamServiceIndex(string.Join(", ", types));
                continue;
            }

            string baseUrl = NormalizeBaseUrl(found.Id);
            forwardMap[routePrefix] = baseUrl;

            if (seenMirrorPrefixes.Add(mirrorPrefix))
            {
                rewritePairs.Add(new RewritePair(baseUrl, mirrorPrefix));
            }
        }
    }

    private async Task<(ServiceIndex Index, string RawJson)> FetchServiceIndexAsync(CancellationToken ct)
    {
        string indexPath = options.Value.Upstream.IndexUrl;
        using HttpResponseMessage response = await client.GetAsync(new Uri(indexPath), stream: false, ct).ConfigureAwait(false);

        byte[] bodyBytes = await BoundedResponseReader.ReadAsync(
            response.Content, options.Value.Upstream.MaxServiceIndexBodyBytes, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Upstream service index body exceeds maximum allowed size.");
        using var reader = new StreamReader(new MemoryStream(bodyBytes),
            response.Content.Headers.ContentType?.CharSet is { } charset ? Encoding.GetEncoding(charset.Trim('"')) : Encoding.UTF8);
        string rawJson = await reader.ReadToEndAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Upstream service index returned {(int)response.StatusCode}: {rawJson}");
        }

        ServiceIndex index = JsonSerializer.Deserialize(rawJson, MirrorJsonContext.Default.ServiceIndex)
            ?? throw new InvalidOperationException("Failed to deserialize upstream service index.");

        return (index, rawJson);
    }

    private static void AppendExtraRewriteHosts(
        IEnumerable<string> extraRewriteHosts,
        List<RewritePair> rewritePairs)
    {
        foreach (string extraHost in extraRewriteHosts)
        {
            string extraBase = extraHost.EndsWith('/') ? extraHost : extraHost + "/";
            rewritePairs.Add(new RewritePair(extraBase, "/v3/registration-semver2/"));
        }
    }

    private static ServiceResource? TryFindResourceByType(List<ServiceResource> resources, string[] types)
    {
        foreach (string type in types)
        {
            for (int i = 0; i < resources.Count; i++)
            {
                if (string.Equals(resources[i].Type, type, StringComparison.Ordinal))
                {
                    return resources[i];
                }
            }
        }

        return null;
    }

    private static string NormalizeBaseUrl(string url)
        => url.EndsWith('/') ? url : url + "/";

    public void Dispose() => _lock.Dispose();

    [LoggerMessage(LogLevel.Error, "Failed to refresh discovery snapshot")]
    private partial void LogFailedToRefreshDiscoverySnapshot(Exception ex);

    [LoggerMessage(LogLevel.Information, "Discovery snapshot refreshed ({Count} resources).")]
    private partial void LogDiscoveryRefreshed(int count);

    [LoggerMessage(LogLevel.Debug, "Using cached discovery snapshot (age: {Age}).")]
    private partial void LogCachedDiscoveryUsed(string age);

    [LoggerMessage(LogLevel.Warning, "Upstream service index missing @type: {Types}")]
    private partial void LogMissingUpstreamServiceIndex(string types);

    [LoggerMessage(LogLevel.Debug, "Upstream service index does not advertise a catalog resource.")]
    private partial void LogCatalogNotAdvertised();

    [LoggerMessage(LogLevel.Debug, "Upstream service index does not advertise a RepositorySignatures resource.")]
    private partial void LogRepositorySignaturesNotAdvertised();

    [LoggerMessage(LogLevel.Debug, "Upstream service index does not advertise a VulnerabilityInfo resource.")]
    private partial void LogVulnerabilityNotAdvertised();

    [LoggerMessage(LogLevel.Debug, "Upstream service index does not advertise a ReadmeUriTemplate resource.")]
    private partial void LogReadmeUriTemplateNotAdvertised();
}
