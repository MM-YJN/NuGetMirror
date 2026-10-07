using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;

namespace NuGetMirror.Diagnostics;

internal sealed class MirrorMetrics : IDisposable
{
    public const string MeterName = "NuGetMirror";

    private readonly Meter _meter;

    private readonly Counter<long> _cacheRequests;
    private readonly Counter<long> _cacheWrites;
    private readonly Counter<long> _cacheWriteBytes;
    private readonly Counter<long> _servedBytes;
    private readonly Counter<long> _upstreamRequests;
    private readonly Histogram<double> _upstreamRequestDuration;
    private readonly Counter<long> _upstreamBytes;
    private readonly Counter<long> _proxyErrors;
    private readonly Counter<long> _clientCancellations;
    private readonly Counter<long> _evictions;
    private readonly Counter<long> _evictedBytes;
    private readonly Histogram<double> _sweepDuration;
    private readonly Counter<long> _discoveryRefreshes;
    private readonly Histogram<double> _discoveryRefreshDuration;
    private readonly Counter<long> _vulnerabilityRefreshes;
    private readonly Counter<long> _repositorySignaturesRefreshes;
    private readonly Counter<long> _registrationRefreshes;
    private readonly Counter<long> _readmeEvents;
    private readonly Counter<long> _lockContended;

    private readonly CacheStatsState _stats;

    public MirrorMetrics(CacheStatsState? stats = null, string? meterName = null)
    {
        _stats = stats ?? new CacheStatsState(TimeProvider.System);
        _meter = new Meter(meterName ?? MeterName);

        _cacheRequests = _meter.CreateCounter<long>(
            "nugetmirror.cache.requests",
            description: "Number of cache lookup requests.");

        _cacheWrites = _meter.CreateCounter<long>(
            "nugetmirror.cache.writes",
            description: "Number of cache write operations.");

        _cacheWriteBytes = _meter.CreateCounter<long>(
            "nugetmirror.cache.write.bytes",
            "bytes",
            "Number of bytes written to cache.");

        _servedBytes = _meter.CreateCounter<long>(
            "nugetmirror.served.bytes",
            "bytes",
            "Number of bytes served to clients.");

        _upstreamRequests = _meter.CreateCounter<long>(
            "nugetmirror.upstream.requests",
            description: "Number of upstream requests.");

        _upstreamRequestDuration = _meter.CreateHistogram<double>(
            "nugetmirror.upstream.request.duration",
            "s",
            "Duration of upstream requests in seconds.");

        _upstreamBytes = _meter.CreateCounter<long>(
            "nugetmirror.upstream.bytes",
            "bytes",
            "Number of bytes read from upstream.");

        _proxyErrors = _meter.CreateCounter<long>(
            "nugetmirror.proxy.errors",
            description: "Number of proxy error responses.");

        _clientCancellations = _meter.CreateCounter<long>(
            "nugetmirror.client.cancellations",
            description: "Number of client-cancelled requests.");

        _evictions = _meter.CreateCounter<long>(
            "nugetmirror.cache.evictions",
            description: "Number of cache entries evicted.");

        _evictedBytes = _meter.CreateCounter<long>(
            "nugetmirror.cache.evicted.bytes",
            "bytes",
            "Number of bytes freed by eviction.");

        _sweepDuration = _meter.CreateHistogram<double>(
            "nugetmirror.cache.sweep.duration",
            "s",
            "Duration of cache eviction sweeps in seconds.");

        _discoveryRefreshes = _meter.CreateCounter<long>(
            "nugetmirror.discovery.refreshes",
            description: "Number of discovery refresh attempts.");

        _discoveryRefreshDuration = _meter.CreateHistogram<double>(
            "nugetmirror.discovery.refresh.duration",
            "s",
            "Duration of discovery refreshes in seconds.");

        _vulnerabilityRefreshes = _meter.CreateCounter<long>(
            "nugetmirror.vulnerability.refreshes",
            description: "Number of vulnerability data refresh attempts.");

        _repositorySignaturesRefreshes = _meter.CreateCounter<long>(
            "nugetmirror.repository_signatures.refreshes",
            description: "Number of repository signatures data refresh attempts.");

        _registrationRefreshes = _meter.CreateCounter<long>(
            "nugetmirror.registration.refreshes",
            description: "Number of registration data refresh attempts.");

        _readmeEvents = _meter.CreateCounter<long>(
            "nugetmirror.readme.events",
            description: "Number of readme cache lifecycle events.");

        _lockContended = _meter.CreateCounter<long>(
            "nugetmirror.cache.lock.contended",
            description: "Number of times a per-key cache lock was contended.");

        _meter.CreateObservableGauge(
            "nugetmirror.cache.size.bytes",
            () => _stats.SizeBytes,
            "bytes",
            "Current total cache size in bytes.");

        _meter.CreateObservableGauge(
            "nugetmirror.cache.size.entries",
            () => _stats.EntryCount,
            description: "Current number of cached entries.");
    }

    public void RecordCacheHit(string method, string contentType)
    {
        _cacheRequests.Add(1,
            new KeyValuePair<string, object?>("result", "hit"),
            new KeyValuePair<string, object?>("method", method),
            new KeyValuePair<string, object?>("content_type", contentType));
    }

    public void RecordClientNotModified(string method, string contentType)
    {
        _cacheRequests.Add(1,
            new KeyValuePair<string, object?>("result", "client_not_modified"),
            new KeyValuePair<string, object?>("method", method),
            new KeyValuePair<string, object?>("content_type", contentType));
    }

    public void RecordCacheHeadHit(string contentType)
    {
        _cacheRequests.Add(1,
            new KeyValuePair<string, object?>("result", "head_hit"),
            new KeyValuePair<string, object?>("method", "head"),
            new KeyValuePair<string, object?>("content_type", contentType));
    }

    public void RecordCacheHitAfterLock(string contentType)
    {
        _cacheRequests.Add(1,
            new KeyValuePair<string, object?>("result", "hit_after_lock"),
            new KeyValuePair<string, object?>("method", "get"),
            new KeyValuePair<string, object?>("content_type", contentType));
    }

    public void RecordCacheMiss(string method, string contentType)
    {
        _cacheRequests.Add(1,
            new KeyValuePair<string, object?>("result", "miss"),
            new KeyValuePair<string, object?>("method", method),
            new KeyValuePair<string, object?>("content_type", contentType));
    }

    public void RecordCacheWriteCommitted(long bytes)
    {
        _cacheWrites.Add(1, new KeyValuePair<string, object?>("result", "committed"));
        _cacheWriteBytes.Add(bytes);
    }

    public void RecordCacheWriteFailed() => _cacheWrites.Add(1, new KeyValuePair<string, object?>("result", "failed"));

    public void RecordCacheWriteSkipped() => _cacheWrites.Add(1, new KeyValuePair<string, object?>("result", "skipped"));

    public void RecordServedBytes(long bytes, string source)
    {
        _servedBytes.Add(bytes,
            new KeyValuePair<string, object?>("source", source));
    }

    public void RecordUpstreamRequest(int statusCode, string mode, string outcome, double durationSeconds)
    {
        var status = new KeyValuePair<string, object?>("status", statusCode.ToString());
        var modeTag = new KeyValuePair<string, object?>("mode", mode);
        var outcomeTag = new KeyValuePair<string, object?>("outcome", outcome);
        _upstreamRequests.Add(1, status, modeTag, outcomeTag);
        _upstreamRequestDuration.Record(durationSeconds, modeTag);
    }

    public void RecordUpstreamBytes(long bytes) => _upstreamBytes.Add(bytes);

    public void RecordProxyError(string kind, int statusCode)
    {
        _proxyErrors.Add(1,
            new KeyValuePair<string, object?>("kind", kind),
            new KeyValuePair<string, object?>("status", statusCode.ToString()));
    }

    public void RecordClientCancellation(string stage)
    {
        _clientCancellations.Add(1,
            new KeyValuePair<string, object?>("stage", stage));
    }

    public void RecordEviction(string reason)
    {
        _evictions.Add(1,
            new KeyValuePair<string, object?>("reason", reason));
    }

    public void RecordNegativeCacheHit(string contentType)
    {
        _cacheRequests.Add(1,
            new KeyValuePair<string, object?>("result", "neg_hit"),
            new KeyValuePair<string, object?>("method", "get"),
            new KeyValuePair<string, object?>("content_type", contentType));
    }

    public void RecordNegativeCacheStore(string contentType)
        => _cacheRequests.Add(1,
            new KeyValuePair<string, object?>("result", "neg_store"),
            new KeyValuePair<string, object?>("method", "get"),
            new KeyValuePair<string, object?>("content_type", contentType));

    public void RecordEvictedBytes(long bytes) => _evictedBytes.Add(bytes);

    public void RecordLockContended() => _lockContended.Add(1);

    public void RegisterActiveLocksGauge(Func<long> observe)
        => _meter.CreateObservableGauge(
            "nugetmirror.cache.locks.active",
            observe,
            description: "Number of live per-key cache locks.");

    public void RegisterNegativeCacheGauge(Func<long> observe)
        => _meter.CreateObservableGauge(
            "nugetmirror.negative_cache.entries",
            observe,
            description: "Number of entries currently in the negative cache.");

    public void RecordSweepDuration(double seconds) => _sweepDuration.Record(seconds);

    /// <summary>
    /// Updates the <c>nugetmirror.cache.size.bytes</c> and <c>nugetmirror.cache.size.entries</c>
    /// gauges with the current cache size.
    /// </summary>
    public void UpdateCacheSize(long bytes, long entries) => _stats.Update(bytes, entries);

    public void RecordDiscoveryRefresh(double durationSeconds)
    {
        _discoveryRefreshes.Add(1,
            new KeyValuePair<string, object?>("result", "success"));
        _discoveryRefreshDuration.Record(durationSeconds);
    }

    public void RecordDiscoveryStaleFallback()
    {
        _discoveryRefreshes.Add(1,
            new KeyValuePair<string, object?>("result", "stale_fallback"));
    }

    public void RecordDiscoveryFailed()
    {
        _discoveryRefreshes.Add(1,
            new KeyValuePair<string, object?>("result", "failed"));
    }

    public void RecordVulnerabilityRevalidated()
    {
        _vulnerabilityRefreshes.Add(1,
            new KeyValuePair<string, object?>("result", "not_modified"));
    }

    public void RecordVulnerabilityStaleFallback()
    {
        _vulnerabilityRefreshes.Add(1,
            new KeyValuePair<string, object?>("result", "stale_fallback"));
    }

    public void RecordVulnerabilityCacheWriteSkipped()
        => _cacheWrites.Add(1, new KeyValuePair<string, object?>("result", "vuln_skipped"));

    public void RecordReadmeRevalidated()
    {
        _readmeEvents.Add(1,
            new KeyValuePair<string, object?>("result", "not_modified"));
    }

    public void RecordReadmeStaleFallback()
    {
        _readmeEvents.Add(1,
            new KeyValuePair<string, object?>("result", "stale_fallback"));
    }

    public void RecordRepositorySignaturesRevalidated()
    {
        _repositorySignaturesRefreshes.Add(1,
            new KeyValuePair<string, object?>("result", "not_modified"));
    }

    public void RecordRepositorySignaturesStaleFallback()
    {
        _repositorySignaturesRefreshes.Add(1,
            new KeyValuePair<string, object?>("result", "stale_fallback"));
    }

    public void RecordRepositorySignaturesCacheWriteSkipped()
        => _cacheWrites.Add(1, new KeyValuePair<string, object?>("result", "reposign_skipped"));

    public void RecordRegistrationRevalidated()
    {
        _registrationRefreshes.Add(1,
            new KeyValuePair<string, object?>("result", "not_modified"));
    }

    public void RecordRegistrationStaleFallback()
    {
        _registrationRefreshes.Add(1,
            new KeyValuePair<string, object?>("result", "stale_fallback"));
    }

    public void RecordRegistrationCacheWriteSkipped()
        => _cacheWrites.Add(1, new KeyValuePair<string, object?>("result", "registration_skipped"));

    public void Dispose() => _meter.Dispose();
}
