using Microsoft.Extensions.Time.Testing;

using NuGetMirror.Diagnostics;
using NuGetMirror.Storage;
using NuGetMirror.TestKit;

namespace NuGetMirror.UnitTests;

public sealed class NegativeCacheTests
{
    [Fact]
    public void TryGet_ReturnsFalse_WhenKeyNotCached()
    {
        var cache = new NegativeCache(maxEntries: 100, TimeProvider.System);

        bool result = cache.TryGet("missing");

        Assert.False(result);
    }

    [Fact]
    public void StoreAndRetrieve_ReturnsTrue()
    {
        var cache = new NegativeCache(maxEntries: 100, TimeProvider.System);

        cache.Store("key1", TimeSpan.FromSeconds(60));
        bool result = cache.TryGet("key1");

        Assert.True(result);
    }

    [Fact]
    public void Expired_ReturnsFalse()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var cache = new NegativeCache(maxEntries: 100, clock);

        cache.Store("key1", TimeSpan.FromMinutes(30));
        Assert.True(cache.TryGet("key1"));

        clock.Advance(TimeSpan.FromMinutes(31));
        Assert.False(cache.TryGet("key1"));
    }

    [Fact]
    public void MultipleKeys_Independent()
    {
        var cache = new NegativeCache(maxEntries: 100, TimeProvider.System);

        cache.Store("key1", TimeSpan.FromSeconds(60));
        cache.Store("key2", TimeSpan.FromSeconds(60));

        Assert.True(cache.TryGet("key1"));
        Assert.True(cache.TryGet("key2"));
        Assert.False(cache.TryGet("key3"));
    }

    [Fact]
    public void Evicts_WhenMaxEntriesExceeded()
    {
        // Add many entries: each time count exceeds maxEntries, TrimExcess cuts to maxEntries/2.
        // After multiple trim cycles, the final count should be well below the total added.
        var cache = new NegativeCache(maxEntries: 10, TimeProvider.System);
        int keysToAdd = 30;

        for (int i = 0; i < keysToAdd; i++)
        {
            cache.Store($"k{i}", TimeSpan.FromMinutes(10));
        }

        int found = 0;
        for (int i = 0; i < keysToAdd; i++)
        {
            if (cache.TryGet($"k{i}"))
            {
                found++;
            }
        }

        Assert.True(found < 20, $"Trim should have removed many entries, found {found}");
        Assert.True(found > 0, "Expected at least some entries to survive");
    }

    [Fact]
    public void Count_TracksStoredEntries()
    {
        var cache = new NegativeCache(maxEntries: 100, TimeProvider.System);

        Assert.Equal(0, cache.Count);

        cache.Store("key1", TimeSpan.FromMinutes(5));
        cache.Store("key2", TimeSpan.FromMinutes(5));

        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void Count_Decreases_WhenEntryExpires()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var cache = new NegativeCache(maxEntries: 100, clock);

        cache.Store("key1", TimeSpan.FromMinutes(5));
        Assert.Equal(1, cache.Count);

        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.False(cache.TryGet("key1"));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void Count_RegistersObservableGauge()
    {
        var stats = new CacheStatsState(TimeProvider.System);
        using var metrics = new MirrorMetrics(stats, meterName: "NegativeCacheTests.Gauge");
        var cache = new NegativeCache(maxEntries: 100, TimeProvider.System, metrics);

        cache.Store("key1", TimeSpan.FromMinutes(5));

        List<Measurement> measurements = MetricsCapture.CaptureObservable("NegativeCacheTests.Gauge");
        Measurement entry = Assert.Single(measurements, m => m.Instrument == "nugetmirror.negative_cache.entries");
        Assert.Equal(1, entry.Value);
    }
}
