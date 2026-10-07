using NuGetMirror.Diagnostics;
using NuGetMirror.TestKit;

namespace NuGetMirror.UnitTests;

public sealed class MirrorMetricsTests
{
    [Fact]
    public void RecordCacheHit_EmitsContentTypeTag()
    {
        const string MeterName = "MirrorMetricsTests.RecordCacheHit";
        using var metrics = new MirrorMetrics(meterName: MeterName);
        Measurement entry = Assert.Single(MetricsCapture.Capture(MeterName, () => metrics.RecordCacheHit("get", "package")));

        Assert.Equal("nugetmirror.cache.requests", entry.Instrument);
        Assert.Equal("hit", entry.GetTag("result"));
        Assert.Equal("get", entry.GetTag("method"));
        Assert.Equal("package", entry.GetTag("content_type"));
    }

    [Fact]
    public void RecordCacheMiss_EmitsContentTypeTag()
    {
        const string MeterName = "MirrorMetricsTests.RecordCacheMiss";
        using var metrics = new MirrorMetrics(meterName: MeterName);
        Measurement entry = Assert.Single(MetricsCapture.Capture(MeterName, () => metrics.RecordCacheMiss("get", "registration")));

        Assert.Equal("nugetmirror.cache.requests", entry.Instrument);
        Assert.Equal("miss", entry.GetTag("result"));
        Assert.Equal("get", entry.GetTag("method"));
        Assert.Equal("registration", entry.GetTag("content_type"));
    }

    [Fact]
    public void RecordCacheHeadHit_EmitsContentTypeTag()
    {
        const string MeterName = "MirrorMetricsTests.RecordCacheHeadHit";
        using var metrics = new MirrorMetrics(meterName: MeterName);
        Measurement entry = Assert.Single(MetricsCapture.Capture(MeterName, () => metrics.RecordCacheHeadHit("package")));

        Assert.Equal("nugetmirror.cache.requests", entry.Instrument);
        Assert.Equal("head_hit", entry.GetTag("result"));
        Assert.Equal("head", entry.GetTag("method"));
        Assert.Equal("package", entry.GetTag("content_type"));
    }

    [Fact]
    public void RecordCacheHitAfterLock_EmitsContentTypeTag()
    {
        const string MeterName = "MirrorMetricsTests.RecordCacheHitAfterLock";
        using var metrics = new MirrorMetrics(meterName: MeterName);
        Measurement entry = Assert.Single(MetricsCapture.Capture(MeterName, () => metrics.RecordCacheHitAfterLock("registration")));

        Assert.Equal("nugetmirror.cache.requests", entry.Instrument);
        Assert.Equal("hit_after_lock", entry.GetTag("result"));
        Assert.Equal("get", entry.GetTag("method"));
        Assert.Equal("registration", entry.GetTag("content_type"));
    }

    [Fact]
    public void RecordClientNotModified_EmitsContentTypeTag()
    {
        const string MeterName = "MirrorMetricsTests.RecordClientNotModified";
        using var metrics = new MirrorMetrics(meterName: MeterName);
        Measurement entry = Assert.Single(MetricsCapture.Capture(MeterName, () => metrics.RecordClientNotModified("head", "package")));

        Assert.Equal("nugetmirror.cache.requests", entry.Instrument);
        Assert.Equal("client_not_modified", entry.GetTag("result"));
        Assert.Equal("head", entry.GetTag("method"));
        Assert.Equal("package", entry.GetTag("content_type"));
    }

    [Fact]
    public void RecordNegativeCacheHit_EmitsContentTypeTag()
    {
        const string MeterName = "MirrorMetricsTests.RecordNegativeCacheHit";
        using var metrics = new MirrorMetrics(meterName: MeterName);
        Measurement entry = Assert.Single(MetricsCapture.Capture(MeterName, () => metrics.RecordNegativeCacheHit("package")));

        Assert.Equal("nugetmirror.cache.requests", entry.Instrument);
        Assert.Equal("neg_hit", entry.GetTag("result"));
        Assert.Equal("get", entry.GetTag("method"));
        Assert.Equal("package", entry.GetTag("content_type"));
    }

    [Fact]
    public void RecordNegativeCacheStore_EmitsContentTypeTag()
    {
        const string MeterName = "MirrorMetricsTests.RecordNegativeCacheStore";
        using var metrics = new MirrorMetrics(meterName: MeterName);
        Measurement entry = Assert.Single(MetricsCapture.Capture(MeterName, () => metrics.RecordNegativeCacheStore("package")));

        Assert.Equal("nugetmirror.cache.requests", entry.Instrument);
        Assert.Equal("neg_store", entry.GetTag("result"));
        Assert.Equal("get", entry.GetTag("method"));
        Assert.Equal("package", entry.GetTag("content_type"));
    }

    [Fact]
    public void RecordCacheWriteCommitted_EmitsCounterAndBytes()
    {
        const string MeterName = "MirrorMetricsTests.RecordCacheWriteCommitted";
        using var metrics = new MirrorMetrics(meterName: MeterName);
        List<Measurement> measurements = MetricsCapture.Capture(MeterName, () => metrics.RecordCacheWriteCommitted(2048));

        Assert.Equal(2, measurements.Count);
        Measurement write = Assert.Single(measurements, m => m.Instrument == "nugetmirror.cache.writes");
        Assert.Equal(1, write.Value);
        Assert.Equal("committed", write.GetTag("result"));
        Measurement bytes = Assert.Single(measurements, m => m.Instrument == "nugetmirror.cache.write.bytes");
        Assert.Equal(2048, bytes.Value);
    }

    [Fact]
    public void RecordCacheWriteFailed_EmitsFailedTag()
    {
        const string MeterName = "MirrorMetricsTests.RecordCacheWriteFailed";
        using var metrics = new MirrorMetrics(meterName: MeterName);
        Measurement entry = Assert.Single(MetricsCapture.Capture(MeterName, () => metrics.RecordCacheWriteFailed()));

        Assert.Equal("nugetmirror.cache.writes", entry.Instrument);
        Assert.Equal("failed", entry.GetTag("result"));
    }

    [Fact]
    public void RecordCacheWriteSkipped_EmitsSkippedTag()
    {
        const string MeterName = "MirrorMetricsTests.RecordCacheWriteSkipped";
        using var metrics = new MirrorMetrics(meterName: MeterName);
        Measurement entry = Assert.Single(MetricsCapture.Capture(MeterName, () => metrics.RecordCacheWriteSkipped()));

        Assert.Equal("nugetmirror.cache.writes", entry.Instrument);
        Assert.Equal("skipped", entry.GetTag("result"));
    }

    [Fact]
    public void RecordServedBytes_EmitsBytesAndSource()
    {
        const string MeterName = "MirrorMetricsTests.RecordServedBytes";
        using var metrics = new MirrorMetrics(meterName: MeterName);
        Measurement entry = Assert.Single(MetricsCapture.Capture(MeterName, () => metrics.RecordServedBytes(4096, "cache")));

        Assert.Equal("nugetmirror.served.bytes", entry.Instrument);
        Assert.Equal(4096, entry.Value);
        Assert.Equal("cache", entry.GetTag("source"));
    }

    [Fact]
    public void RecordUpstreamRequest_EmitsCounterAndHistogram()
    {
        const string MeterName = "MirrorMetricsTests.RecordUpstreamRequest";
        using var metrics = new MirrorMetrics(meterName: MeterName);
        List<Measurement> measurements = MetricsCapture.Capture(MeterName, () => metrics.RecordUpstreamRequest(200, "package", "success", 1.5));

        Assert.Equal(2, measurements.Count);
        Measurement request = Assert.Single(measurements, m => m.Instrument == "nugetmirror.upstream.requests");
        Assert.Equal(1, request.Value);
        Assert.Equal("200", request.GetTag("status"));
        Assert.Equal("package", request.GetTag("mode"));
        Assert.Equal("success", request.GetTag("outcome"));
        Measurement duration = Assert.Single(measurements, m => m.Instrument == "nugetmirror.upstream.request.duration");
        Assert.Equal(1.5, duration.Value);
        Assert.Equal("package", duration.GetTag("mode"));
    }

    [Fact]
    public void RecordUpstreamBytes_EmitsBytes()
    {
        const string MeterName = "MirrorMetricsTests.RecordUpstreamBytes";
        using var metrics = new MirrorMetrics(meterName: MeterName);
        Measurement entry = Assert.Single(MetricsCapture.Capture(MeterName, () => metrics.RecordUpstreamBytes(8192)));

        Assert.Equal("nugetmirror.upstream.bytes", entry.Instrument);
        Assert.Equal(8192, entry.Value);
    }

    [Fact]
    public void RecordProxyError_EmitsKindAndStatus()
    {
        const string MeterName = "MirrorMetricsTests.RecordProxyError";
        using var metrics = new MirrorMetrics(meterName: MeterName);
        Measurement entry = Assert.Single(MetricsCapture.Capture(MeterName, () => metrics.RecordProxyError("timeout", 504)));

        Assert.Equal("nugetmirror.proxy.errors", entry.Instrument);
        Assert.Equal("timeout", entry.GetTag("kind"));
        Assert.Equal("504", entry.GetTag("status"));
    }

    [Fact]
    public void RecordClientCancellation_EmitsStage()
    {
        const string MeterName = "MirrorMetricsTests.RecordClientCancellation";
        using var metrics = new MirrorMetrics(meterName: MeterName);
        Measurement entry = Assert.Single(MetricsCapture.Capture(MeterName, () => metrics.RecordClientCancellation("streaming")));

        Assert.Equal("nugetmirror.client.cancellations", entry.Instrument);
        Assert.Equal("streaming", entry.GetTag("stage"));
    }

    [Fact]
    public void RecordEviction_EmitsReason()
    {
        const string MeterName = "MirrorMetricsTests.RecordEviction";
        using var metrics = new MirrorMetrics(meterName: MeterName);
        Measurement entry = Assert.Single(MetricsCapture.Capture(MeterName, () => metrics.RecordEviction("max_age")));

        Assert.Equal("nugetmirror.cache.evictions", entry.Instrument);
        Assert.Equal("max_age", entry.GetTag("reason"));
    }

    [Fact]
    public void RecordEvictedBytes_EmitsBytes()
    {
        const string MeterName = "MirrorMetricsTests.RecordEvictedBytes";
        using var metrics = new MirrorMetrics(meterName: MeterName);
        Measurement entry = Assert.Single(MetricsCapture.Capture(MeterName, () => metrics.RecordEvictedBytes(16384)));

        Assert.Equal("nugetmirror.cache.evicted.bytes", entry.Instrument);
        Assert.Equal(16384, entry.Value);
    }

    [Fact]
    public void RecordLockContended_EmitsCounter()
    {
        const string MeterName = "MirrorMetricsTests.RecordLockContended";
        using var metrics = new MirrorMetrics(meterName: MeterName);
        Measurement entry = Assert.Single(MetricsCapture.Capture(MeterName, () => metrics.RecordLockContended()));

        Assert.Equal("nugetmirror.cache.lock.contended", entry.Instrument);
        Assert.Equal(1, entry.Value);
    }

    [Fact]
    public void RegisterActiveLocksGauge_EmitsObservedValue()
    {
        const string MeterName = "MirrorMetricsTests.RegisterActiveLocksGauge";
        using var metrics = new MirrorMetrics(meterName: MeterName);
        metrics.RegisterActiveLocksGauge(() => 5);

        List<Measurement> measurements = MetricsCapture.CaptureObservable(MeterName);
        Measurement entry = Assert.Single(measurements, m => m.Instrument == "nugetmirror.cache.locks.active");
        Assert.Equal(5, entry.Value);
    }

    [Fact]
    public void RegisterNegativeCacheGauge_EmitsObservedValue()
    {
        const string MeterName = "MirrorMetricsTests.RegisterNegativeCacheGauge";
        using var metrics = new MirrorMetrics(meterName: MeterName);
        metrics.RegisterNegativeCacheGauge(() => 3);

        List<Measurement> measurements = MetricsCapture.CaptureObservable(MeterName);
        Measurement entry = Assert.Single(measurements, m => m.Instrument == "nugetmirror.negative_cache.entries");
        Assert.Equal(3, entry.Value);
    }

    [Fact]
    public void RecordSweepDuration_EmitsHistogramValue()
    {
        const string MeterName = "MirrorMetricsTests.RecordSweepDuration";
        using var metrics = new MirrorMetrics(meterName: MeterName);
        Measurement entry = Assert.Single(MetricsCapture.Capture(MeterName, () => metrics.RecordSweepDuration(2.5)));

        Assert.Equal("nugetmirror.cache.sweep.duration", entry.Instrument);
        Assert.Equal(2.5, entry.Value);
    }

    [Fact]
    public void UpdateCacheSize_UpdatesObservableGauges()
    {
        const string MeterName = "MirrorMetricsTests.UpdateCacheSize";
        var stats = new CacheStatsState(TimeProvider.System);
        using var metrics = new MirrorMetrics(stats: stats, meterName: MeterName);

        metrics.UpdateCacheSize(4096, 7);

        List<Measurement> measurements = MetricsCapture.CaptureObservable(MeterName);
        Measurement bytes = Assert.Single(measurements, m => m.Instrument == "nugetmirror.cache.size.bytes");
        Assert.Equal(4096, bytes.Value);
        Measurement entries = Assert.Single(measurements, m => m.Instrument == "nugetmirror.cache.size.entries");
        Assert.Equal(7, entries.Value);
    }

    [Fact]
    public void RecordDiscoveryRefresh_EmitsCounterAndHistogram()
    {
        const string MeterName = "MirrorMetricsTests.RecordDiscoveryRefresh";
        using var metrics = new MirrorMetrics(meterName: MeterName);
        List<Measurement> measurements = MetricsCapture.Capture(MeterName, () => metrics.RecordDiscoveryRefresh(0.25));

        Assert.Equal(2, measurements.Count);
        Measurement refresh = Assert.Single(measurements, m => m.Instrument == "nugetmirror.discovery.refreshes");
        Assert.Equal("success", refresh.GetTag("result"));
        Measurement duration = Assert.Single(measurements, m => m.Instrument == "nugetmirror.discovery.refresh.duration");
        Assert.Equal(0.25, duration.Value);
    }

    [Fact]
    public void RecordDiscoveryStaleFallback_EmitsStaleFallbackTag()
    {
        const string MeterName = "MirrorMetricsTests.RecordDiscoveryStaleFallback";
        using var metrics = new MirrorMetrics(meterName: MeterName);
        Measurement entry = Assert.Single(MetricsCapture.Capture(MeterName, () => metrics.RecordDiscoveryStaleFallback()));

        Assert.Equal("nugetmirror.discovery.refreshes", entry.Instrument);
        Assert.Equal("stale_fallback", entry.GetTag("result"));
    }

    [Fact]
    public void RecordDiscoveryFailed_EmitsFailedTag()
    {
        const string MeterName = "MirrorMetricsTests.RecordDiscoveryFailed";
        using var metrics = new MirrorMetrics(meterName: MeterName);
        Measurement entry = Assert.Single(MetricsCapture.Capture(MeterName, () => metrics.RecordDiscoveryFailed()));

        Assert.Equal("nugetmirror.discovery.refreshes", entry.Instrument);
        Assert.Equal("failed", entry.GetTag("result"));
    }

    [Fact]
    public void RecordVulnerabilityRevalidated_EmitsNotModifiedTag()
    {
        const string MeterName = "MirrorMetricsTests.RecordVulnerabilityRevalidated";
        using var metrics = new MirrorMetrics(meterName: MeterName);
        Measurement entry = Assert.Single(MetricsCapture.Capture(MeterName, () => metrics.RecordVulnerabilityRevalidated()));

        Assert.Equal("nugetmirror.vulnerability.refreshes", entry.Instrument);
        Assert.Equal("not_modified", entry.GetTag("result"));
    }

    [Fact]
    public void RecordVulnerabilityStaleFallback_EmitsStaleFallbackTag()
    {
        const string MeterName = "MirrorMetricsTests.RecordVulnerabilityStaleFallback";
        using var metrics = new MirrorMetrics(meterName: MeterName);
        Measurement entry = Assert.Single(MetricsCapture.Capture(MeterName, () => metrics.RecordVulnerabilityStaleFallback()));

        Assert.Equal("nugetmirror.vulnerability.refreshes", entry.Instrument);
        Assert.Equal("stale_fallback", entry.GetTag("result"));
    }

    [Fact]
    public void RecordVulnerabilityCacheWriteSkipped_EmitsVulnSkippedTag()
    {
        const string MeterName = "MirrorMetricsTests.RecordVulnerabilityCacheWriteSkipped";
        using var metrics = new MirrorMetrics(meterName: MeterName);
        Measurement entry = Assert.Single(MetricsCapture.Capture(MeterName, () => metrics.RecordVulnerabilityCacheWriteSkipped()));

        Assert.Equal("nugetmirror.cache.writes", entry.Instrument);
        Assert.Equal("vuln_skipped", entry.GetTag("result"));
    }

    [Fact]
    public void RecordReadmeRevalidated_EmitsNotModifiedTag()
    {
        const string MeterName = "MirrorMetricsTests.RecordReadmeRevalidated";
        using var metrics = new MirrorMetrics(meterName: MeterName);
        Measurement entry = Assert.Single(MetricsCapture.Capture(MeterName, () => metrics.RecordReadmeRevalidated()));

        Assert.Equal("nugetmirror.readme.events", entry.Instrument);
        Assert.Equal("not_modified", entry.GetTag("result"));
    }

    [Fact]
    public void RecordReadmeStaleFallback_EmitsStaleFallbackTag()
    {
        const string MeterName = "MirrorMetricsTests.RecordReadmeStaleFallback";
        using var metrics = new MirrorMetrics(meterName: MeterName);
        Measurement entry = Assert.Single(MetricsCapture.Capture(MeterName, () => metrics.RecordReadmeStaleFallback()));

        Assert.Equal("nugetmirror.readme.events", entry.Instrument);
        Assert.Equal("stale_fallback", entry.GetTag("result"));
    }

    [Fact]
    public void RecordRepositorySignaturesRevalidated_EmitsNotModifiedTag()
    {
        const string MeterName = "MirrorMetricsTests.RecordRepositorySignaturesRevalidated";
        using var metrics = new MirrorMetrics(meterName: MeterName);
        Measurement entry = Assert.Single(MetricsCapture.Capture(MeterName, () => metrics.RecordRepositorySignaturesRevalidated()));

        Assert.Equal("nugetmirror.repository_signatures.refreshes", entry.Instrument);
        Assert.Equal("not_modified", entry.GetTag("result"));
    }

    [Fact]
    public void RecordRepositorySignaturesStaleFallback_EmitsStaleFallbackTag()
    {
        const string MeterName = "MirrorMetricsTests.RecordRepositorySignaturesStaleFallback";
        using var metrics = new MirrorMetrics(meterName: MeterName);
        Measurement entry = Assert.Single(MetricsCapture.Capture(MeterName, () => metrics.RecordRepositorySignaturesStaleFallback()));

        Assert.Equal("nugetmirror.repository_signatures.refreshes", entry.Instrument);
        Assert.Equal("stale_fallback", entry.GetTag("result"));
    }

    [Fact]
    public void RecordRepositorySignaturesCacheWriteSkipped_EmitsReposignSkippedTag()
    {
        const string MeterName = "MirrorMetricsTests.RecordRepositorySignaturesCacheWriteSkipped";
        using var metrics = new MirrorMetrics(meterName: MeterName);
        Measurement entry = Assert.Single(MetricsCapture.Capture(MeterName, () => metrics.RecordRepositorySignaturesCacheWriteSkipped()));

        Assert.Equal("nugetmirror.cache.writes", entry.Instrument);
        Assert.Equal("reposign_skipped", entry.GetTag("result"));
    }

    [Fact]
    public void RecordRegistrationRevalidated_EmitsNotModifiedTag()
    {
        const string MeterName = "MirrorMetricsTests.RecordRegistrationRevalidated";
        using var metrics = new MirrorMetrics(meterName: MeterName);
        Measurement entry = Assert.Single(MetricsCapture.Capture(MeterName, () => metrics.RecordRegistrationRevalidated()));

        Assert.Equal("nugetmirror.registration.refreshes", entry.Instrument);
        Assert.Equal("not_modified", entry.GetTag("result"));
    }

    [Fact]
    public void RecordRegistrationStaleFallback_EmitsStaleFallbackTag()
    {
        const string MeterName = "MirrorMetricsTests.RecordRegistrationStaleFallback";
        using var metrics = new MirrorMetrics(meterName: MeterName);
        Measurement entry = Assert.Single(MetricsCapture.Capture(MeterName, () => metrics.RecordRegistrationStaleFallback()));

        Assert.Equal("nugetmirror.registration.refreshes", entry.Instrument);
        Assert.Equal("stale_fallback", entry.GetTag("result"));
    }

    [Fact]
    public void RecordRegistrationCacheWriteSkipped_EmitsRegistrationSkippedTag()
    {
        const string MeterName = "MirrorMetricsTests.RecordRegistrationCacheWriteSkipped";
        using var metrics = new MirrorMetrics(meterName: MeterName);
        Measurement entry = Assert.Single(MetricsCapture.Capture(MeterName, () => metrics.RecordRegistrationCacheWriteSkipped()));

        Assert.Equal("nugetmirror.cache.writes", entry.Instrument);
        Assert.Equal("registration_skipped", entry.GetTag("result"));
    }

    [Fact]
    public void Dispose_StopsRecordingMeasurements()
    {
        const string MeterName = "MirrorMetricsTests.Dispose";
        var metrics = new MirrorMetrics(meterName: MeterName);

        Assert.Single(MetricsCapture.Capture(MeterName, () => metrics.RecordCacheHit("get", "package")));

        metrics.Dispose();

        Assert.Empty(MetricsCapture.Capture(MeterName, () => metrics.RecordCacheHit("get", "package")));
    }
}
