using System.Reflection;
using System.Runtime.CompilerServices;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using NuGetMirror.Configuration;
using NuGetMirror.Diagnostics;
using NuGetMirror.Storage;

namespace NuGetMirror.UnitTests;

public sealed class CacheEvictionServiceTests
{
    [Fact]
    public async Task Sweep_EvictsMaxAgeEntries()
    {
        var store = new FakeMaintenanceStore();
        DateTimeOffset oldTime = DateTimeOffset.UtcNow.AddHours(-2);
        DateTimeOffset recentTime = DateTimeOffset.UtcNow.AddMinutes(-5);
        store.AddEntry("old.pkg/1.0.0/old.pkg.nupkg", 1000, oldTime);
        store.AddEntry("recent.pkg/1.0.0/recent.pkg.nupkg", 2000, recentTime);

        var options = new CacheEvictionOptions
        {
            Enabled = true,
            Strategy = EvictionStrategy.Oldest,
            MaxAge = TimeSpan.FromHours(1),
            MaxSizeBytes = null,
        };

        CacheEvictionService service = CreateService(store, options);

        await InvokeSweepAsync(service, store, options);

        Assert.Single(store.DeletedKeys);
        Assert.Equal("old.pkg/1.0.0/old.pkg.nupkg", store.DeletedKeys[0]);
        Assert.DoesNotContain("recent.pkg/1.0.0/recent.pkg.nupkg", store.DeletedKeys);
    }

    [Fact]
    public async Task Sweep_EvictsOldestEntries_WhenOverSizeCap()
    {
        var store = new FakeMaintenanceStore();
        DateTimeOffset time1 = DateTimeOffset.UtcNow.AddHours(-3);
        DateTimeOffset time2 = DateTimeOffset.UtcNow.AddHours(-2);
        DateTimeOffset time3 = DateTimeOffset.UtcNow.AddHours(-1);
        store.AddEntry("pkg.oldest/1.0.0/pkg.oldest.nupkg", 1000, time1);
        store.AddEntry("pkg.middle/1.0.0/pkg.middle.nupkg", 1000, time2);
        store.AddEntry("pkg.newest/1.0.0/pkg.newest.nupkg", 1000, time3);

        var options = new CacheEvictionOptions
        {
            Enabled = true,
            Strategy = EvictionStrategy.Oldest,
            MaxSizeBytes = 1500,
            MaxAge = null,
            TargetUtilization = 1.0,
        };

        CacheEvictionService service = CreateService(store, options);

        await InvokeSweepAsync(service, store, options);

        Assert.Equal(2, store.DeletedKeys.Count);
        Assert.Equal("pkg.oldest/1.0.0/pkg.oldest.nupkg", store.DeletedKeys[0]);
        Assert.Equal("pkg.middle/1.0.0/pkg.middle.nupkg", store.DeletedKeys[1]);
        Assert.DoesNotContain("pkg.newest/1.0.0/pkg.newest.nupkg", store.DeletedKeys);
    }

    [Fact]
    public async Task Sweep_EvictsToTargetUtilization()
    {
        var store = new FakeMaintenanceStore();
        DateTimeOffset baseTime = DateTimeOffset.UtcNow.AddHours(-1);
        for (int i = 0; i < 10; i++)
        {
            store.AddEntry($"pkg{i}/1.0.0/pkg{i}.nupkg", 1000, baseTime.AddMinutes(i));
        }

        var options = new CacheEvictionOptions
        {
            Enabled = true,
            Strategy = EvictionStrategy.Oldest,
            MaxSizeBytes = 9000,
            MaxAge = null,
            TargetUtilization = 0.5,
        };

        CacheEvictionService service = CreateService(store, options);

        await InvokeSweepAsync(service, store, options);

        Assert.Equal(6, store.DeletedKeys.Count);
    }

    [Fact]
    public async Task Sweep_AppliesMaxAgeBeforeSizeCap()
    {
        var store = new FakeMaintenanceStore();
        DateTimeOffset oldTime = DateTimeOffset.UtcNow.AddHours(-3);
        DateTimeOffset recentTime = DateTimeOffset.UtcNow.AddMinutes(-5);
        store.AddEntry("old.pkg1/1.0.0/old.pkg1.nupkg", 2000, oldTime);
        store.AddEntry("old.pkg2/1.0.0/old.pkg2.nupkg", 1000, oldTime);
        store.AddEntry("recent.pkg/1.0.0/recent.pkg.nupkg", 500, recentTime);

        var options = new CacheEvictionOptions
        {
            Enabled = true,
            Strategy = EvictionStrategy.Oldest,
            MaxAge = TimeSpan.FromHours(1),
            MaxSizeBytes = 100,
            TargetUtilization = 1.0,
        };

        CacheEvictionService service = CreateService(store, options);

        await InvokeSweepAsync(service, store, options);

        Assert.Contains("old.pkg1/1.0.0/old.pkg1.nupkg", store.DeletedKeys);
        Assert.Contains("old.pkg2/1.0.0/old.pkg2.nupkg", store.DeletedKeys);
        Assert.Single(store.DeletedKeys, k => k == "recent.pkg/1.0.0/recent.pkg.nupkg");
    }

    [Fact]
    public async Task Sweep_AllEntriesWithinBounds_NoEviction()
    {
        var store = new FakeMaintenanceStore();
        store.AddEntry("pkg.a/1.0.0/pkg.a.nupkg", 1000, DateTimeOffset.UtcNow.AddMinutes(-5));
        store.AddEntry("pkg.b/1.0.0/pkg.b.nupkg", 2000, DateTimeOffset.UtcNow.AddMinutes(-10));

        var options = new CacheEvictionOptions
        {
            Enabled = true,
            MaxAge = TimeSpan.FromHours(1),
            MaxSizeBytes = 5000,
        };

        CacheEvictionService service = CreateService(store, options);

        await InvokeSweepAsync(service, store, options);

        Assert.Empty(store.DeletedKeys);
    }

    [Fact]
    public async Task Sweep_NoOp_WhenDisabled()
    {
        var store = new FakeMaintenanceStore();
        store.AddEntry("pkg.a/1.0.0/pkg.a.nupkg", 1000, DateTimeOffset.UtcNow.AddHours(-3));

        var mirrorOptions = new MirrorOptions
        {
            Cache = new CacheOptions
            {
                Enabled = true,
                Eviction = new CacheEvictionOptions
                {
                    Enabled = false,
                    MaxAge = TimeSpan.FromHours(1),
                },
            },
        };

        var service = new CacheEvictionService(store, Microsoft.Extensions.Options.Options.Create(mirrorOptions), NullLogger<CacheEvictionService>.Instance, new MirrorMetrics(), TimeProvider.System);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await service.StartAsync(cts.Token);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        await cts.CancelAsync();

        Assert.Empty(store.DeletedKeys);
    }

    [Fact]
    public async Task Sweep_NoOp_WhenStoreNotICacheMaintenance()
    {
        var store = new FakeStoreNoMaintenance();
        var mirrorOptions = new MirrorOptions
        {
            Cache = new CacheOptions
            {
                Enabled = true,
                Eviction = new CacheEvictionOptions
                {
                    Enabled = true,
                    MaxAge = TimeSpan.FromHours(1),
                },
            },
        };

        var service = new CacheEvictionService(store, Microsoft.Extensions.Options.Options.Create(mirrorOptions), NullLogger<CacheEvictionService>.Instance, new MirrorMetrics(), TimeProvider.System);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await service.StartAsync(cts.Token);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
    }

    [Fact]
    public async Task Sweep_ToleratesDeleteErrors()
    {
        var store = new FakeErroringStore();
        store.AddEntry("pkg.a/1.0.0/pkg.a.nupkg", 1000, DateTimeOffset.UtcNow.AddHours(-2));
        store.AddEntry("pkg.b/1.0.0/pkg.b.nupkg", 1000, DateTimeOffset.UtcNow.AddHours(-2));

        var options = new CacheEvictionOptions
        {
            Enabled = true,
            MaxAge = TimeSpan.FromHours(1),
        };

        CacheEvictionService service = CreateService(store, options);

        await InvokeSweepAsync(service, store, options);
    }

    [Fact]
    public async Task EnumerateStatsOnly_UpdatesCacheStats_WhenReportingEnabled()
    {
        var store = new FakeMaintenanceStore();
        store.AddEntry("pkg.a/1.0.0/pkg.a.nupkg", 1000, DateTimeOffset.UtcNow.AddMinutes(-5));
        store.AddEntry("pkg.b/1.0.0/pkg.b.nupkg", 2000, DateTimeOffset.UtcNow.AddMinutes(-10));

        var options = new CacheEvictionOptions
        {
            Enabled = false,
        };

        var mirrorOptions = new MirrorOptions
        {
            Cache = new CacheOptions
            {
                Enabled = true,
                Eviction = options,
                SizeReporting = new CacheSizeReportingOptions { Enabled = true },
            },
        };

        var stats = new CacheStatsState(TimeProvider.System);
        var metrics = new MirrorMetrics(stats);
        var service = new CacheEvictionService(store, Options.Create(mirrorOptions), NullLogger<CacheEvictionService>.Instance, metrics, TimeProvider.System);

        MethodInfo method = typeof(CacheEvictionService).GetMethod(
            "EnumerateStatsOnlyAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?? throw new InvalidOperationException("CacheEvictionService.EnumerateStatsOnlyAsync method not found.");

        var task = (Task)(method.Invoke(service, [store, TestContext.Current.CancellationToken])
            ?? throw new InvalidOperationException("Invoke returned null."));
        await task;

        Assert.Equal(3000, stats.SizeBytes);
        Assert.Equal(2, stats.EntryCount);
        Assert.NotEqual(DateTimeOffset.MinValue, stats.LastUpdateUtc);
    }

    [Fact]
    public async Task Sweep_EvictsEntry_WhenClockAdvancesPastMaxAge()
    {
        var store = new FakeMaintenanceStore();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        store.AddEntry("pkg.a/1.0.0/pkg.a.nupkg", 1000, clock.GetUtcNow().AddMinutes(-5));

        var options = new CacheEvictionOptions
        {
            Enabled = true,
            MaxAge = TimeSpan.FromHours(1),
            MaxSizeBytes = null,
        };

        CacheEvictionService service = CreateService(store, options, clock);

        // First sweep — entry is fresh, nothing evicted.
        await InvokeSweepAsync(service, store, options);
        Assert.Empty(store.DeletedKeys);

        // Advance clock past MaxAge — entry is now stale and should be evicted.
        clock.Advance(TimeSpan.FromHours(2));
        await InvokeSweepAsync(service, store, options);
        Assert.Single(store.DeletedKeys);
        Assert.Equal("pkg.a/1.0.0/pkg.a.nupkg", store.DeletedKeys[0]);
    }

    private static CacheEvictionService CreateService(IPackageContentStore store, CacheEvictionOptions eviction, TimeProvider? timeProvider = null)
    {
        var mirrorOptions = new MirrorOptions
        {
            Cache = new CacheOptions
            {
                Enabled = true,
                Eviction = eviction,
            },
        };

        return new CacheEvictionService(store, Options.Create(mirrorOptions), NullLogger<CacheEvictionService>.Instance, new MirrorMetrics(), timeProvider ?? TimeProvider.System);
    }

    private static async Task InvokeSweepAsync(
        CacheEvictionService service,
        ICacheMaintenance maintenance,
        CacheEvictionOptions eviction)
    {
        MethodInfo method = typeof(CacheEvictionService).GetMethod(
            "SweepAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?? throw new InvalidOperationException("CacheEvictionService.SweepAsync method not found.");

        var task = (Task)(method.Invoke(service, [maintenance, eviction, TestContext.Current.CancellationToken])
            ?? throw new InvalidOperationException("Invoke returned null."));
        await task;
    }

    private abstract class FakeMaintenanceStoreBase : IPackageContentStore, ICacheMaintenance
    {
        protected readonly List<CacheEntryInfo> _entries = [];

        public int EnumerateCalls { get; set; }

        public TaskCompletionSource PassStarted { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource EnumerationCompleted { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void AddEntry(string key, long length, DateTimeOffset lastModifiedUtc)
            => _entries.Add(new CacheEntryInfo(key, length, lastModifiedUtc));

        public ValueTask<CachedContent?> TryGetAsync(string key, CancellationToken ct)
            => ValueTask.FromResult<CachedContent?>(null);

        public ValueTask<ICacheWriteHandle> BeginWriteAsync(string key, CancellationToken ct)
            => throw new NotSupportedException();

        public ValueTask TouchAsync(string key, CancellationToken ct)
            => ValueTask.CompletedTask;

        public virtual ValueTask DeleteAsync(string key, CancellationToken ct)
            => ValueTask.CompletedTask;

        public async IAsyncEnumerable<CacheEntryInfo> EnumerateAsync([EnumeratorCancellation] CancellationToken ct)
        {
            EnumerateCalls++;
            PassStarted.TrySetResult();

            try
            {
                await foreach (CacheEntryInfo entry in EnumerateCoreAsync(ct))
                {
                    yield return entry;
                }
            }
            finally
            {
                EnumerationCompleted.TrySetResult();
            }
        }

        protected virtual async IAsyncEnumerable<CacheEntryInfo> EnumerateCoreAsync([EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            foreach (CacheEntryInfo entry in _entries)
            {
                ct.ThrowIfCancellationRequested();
                yield return entry;
            }
        }
    }

    private sealed class FakeMaintenanceStore : FakeMaintenanceStoreBase
    {
        public List<string> DeletedKeys { get; } = [];

        public override ValueTask DeleteAsync(string key, CancellationToken ct)
        {
            DeletedKeys.Add(key);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingMaintenanceStore(Exception exception, int throwOnCall = 1) : FakeMaintenanceStoreBase
    {
        protected override async IAsyncEnumerable<CacheEntryInfo> EnumerateCoreAsync([EnumeratorCancellation] CancellationToken ct)
        {
            if (EnumerateCalls == throwOnCall)
            {
                await Task.CompletedTask;
                throw exception;
            }

            await foreach (CacheEntryInfo entry in base.EnumerateCoreAsync(ct))
            {
                yield return entry;
            }
        }
    }

    private sealed class BlockingMaintenanceStore : FakeMaintenanceStoreBase
    {
        protected override async IAsyncEnumerable<CacheEntryInfo> EnumerateCoreAsync([EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);

            // Unreachable — the delay above either blocks forever or throws on cancellation.
            // Satisfies the compiler requirement for at least one yield in an async iterator.
            yield break;
        }
    }

    private sealed class FakeStoreNoMaintenance : IPackageContentStore
    {
        public ValueTask<CachedContent?> TryGetAsync(string key, CancellationToken ct)
            => ValueTask.FromResult<CachedContent?>(null);

        public ValueTask<ICacheWriteHandle> BeginWriteAsync(string key, CancellationToken ct)
            => throw new NotSupportedException();
    }

    private sealed class FakeErroringStore : FakeMaintenanceStoreBase
    {
        public override ValueTask DeleteAsync(string key, CancellationToken ct)
            => ValueTask.FromException(new InvalidOperationException("Simulated delete failure"));
    }

    [Fact]
    public async Task Sweep_EvictsReadmeAndVulnerabilityEntries()
    {
        var store = new FakeMaintenanceStore();
        DateTimeOffset oldTime = DateTimeOffset.UtcNow.AddHours(-2);
        store.AddEntry("$readme/some.pkg/1.0.0/readme.md", 500, oldTime);
        store.AddEntry("$vuln/index.json", 2000, oldTime);
        store.AddEntry("$vuln/page/2024.01.01/index.json", 1000, oldTime);
        store.AddEntry("$reposign/1.0.0/index.json", 1500, oldTime);
        store.AddEntry("$reposign/certificates/abc123.crt", 800, oldTime);
        store.AddEntry("$registration/semver2/newtonsoft.json/index.json", 1200, oldTime);
        store.AddEntry("some.pkg/1.0.0/some.pkg.nupkg", 3000, oldTime);

        var options = new CacheEvictionOptions
        {
            Enabled = true,
            MaxAge = TimeSpan.FromHours(1),
            MaxSizeBytes = null,
        };

        CacheEvictionService service = CreateService(store, options);

        await InvokeSweepAsync(service, store, options);

        Assert.Equal(7, store.DeletedKeys.Count);
        Assert.Contains("$readme/some.pkg/1.0.0/readme.md", store.DeletedKeys);
        Assert.Contains("$vuln/index.json", store.DeletedKeys);
        Assert.Contains("$vuln/page/2024.01.01/index.json", store.DeletedKeys);
        Assert.Contains("$reposign/1.0.0/index.json", store.DeletedKeys);
        Assert.Contains("$reposign/certificates/abc123.crt", store.DeletedKeys);
        Assert.Contains("$registration/semver2/newtonsoft.json/index.json", store.DeletedKeys);
        Assert.Contains("some.pkg/1.0.0/some.pkg.nupkg", store.DeletedKeys);
    }

    [Fact]
    public async Task Sweep_EvictsMetadataEntries_WhenOverSizeCap()
    {
        var store = new FakeMaintenanceStore();
        DateTimeOffset baseTime = DateTimeOffset.UtcNow.AddHours(-1);
        store.AddEntry("$readme/pkg/1.0.0/readme.md", 1000, baseTime);
        store.AddEntry("$vuln/index.json", 1000, baseTime.AddMinutes(1));
        store.AddEntry("$registration/semver2/pkg/index.json", 1000, baseTime.AddMinutes(2));
        store.AddEntry("pkg.a/1.0.0/pkg.a.nupkg", 1000, baseTime.AddMinutes(3));

        var options = new CacheEvictionOptions
        {
            Enabled = true,
            MaxSizeBytes = 1500,
            MaxAge = null,
            TargetUtilization = 1.0,
        };

        CacheEvictionService service = CreateService(store, options);

        await InvokeSweepAsync(service, store, options);

        Assert.Equal(3, store.DeletedKeys.Count);
        Assert.Contains("$readme/pkg/1.0.0/readme.md", store.DeletedKeys);
        Assert.Contains("$vuln/index.json", store.DeletedKeys);
        Assert.Contains("$registration/semver2/pkg/index.json", store.DeletedKeys);
        Assert.DoesNotContain("pkg.a/1.0.0/pkg.a.nupkg", store.DeletedKeys);
    }

    [Fact]
    public async Task ExecuteAsync_DispatchesSweep_WhenEvictionEnabled()
    {
        var store = new FakeMaintenanceStore();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        store.AddEntry("pkg.a/1.0.0/pkg.a.nupkg", 1000, clock.GetUtcNow().AddHours(-2));

        var options = new CacheEvictionOptions
        {
            Enabled = true,
            MaxAge = TimeSpan.FromHours(1),
            Interval = TimeSpan.FromDays(1),
        };

        var stats = new CacheStatsState(TimeProvider.System);
        var metrics = new MirrorMetrics(stats);
        var service = new CacheEvictionService(store, Options.Create(new MirrorOptions
        {
            Cache = new CacheOptions
            {
                Enabled = true,
                Eviction = options,
            },
        }), NullLogger<CacheEvictionService>.Instance, metrics, clock);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await store.EnumerationCompleted.Task.WaitAsync(TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);

        Assert.Single(store.DeletedKeys);
        Assert.Equal(0, stats.EntryCount);
        Assert.Equal(0, stats.SizeBytes);
    }

    [Fact]
    public async Task ExecuteAsync_DispatchesStatsOnly_WhenReportingEnabledAndEvictionDisabled()
    {
        var store = new FakeMaintenanceStore();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        store.AddEntry("pkg.a/1.0.0/pkg.a.nupkg", 1000, clock.GetUtcNow().AddMinutes(-5));
        store.AddEntry("pkg.b/1.0.0/pkg.b.nupkg", 2000, clock.GetUtcNow().AddMinutes(-10));

        var stats = new CacheStatsState(TimeProvider.System);
        var metrics = new MirrorMetrics(stats);
        var service = new CacheEvictionService(store, Options.Create(new MirrorOptions
        {
            Cache = new CacheOptions
            {
                Enabled = true,
                Eviction = new CacheEvictionOptions { Enabled = false },
                SizeReporting = new CacheSizeReportingOptions { Enabled = true },
            },
        }), NullLogger<CacheEvictionService>.Instance, metrics, clock);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await store.EnumerationCompleted.Task.WaitAsync(TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3000, stats.SizeBytes);
        Assert.Equal(2, stats.EntryCount);
        Assert.NotEqual(DateTimeOffset.MinValue, stats.LastUpdateUtc);
    }

    [Fact]
    public async Task ExecuteAsync_NoOp_WhenCacheDisabled()
    {
        var store = new FakeMaintenanceStore();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        store.AddEntry("pkg.a/1.0.0/pkg.a.nupkg", 1000, clock.GetUtcNow().AddHours(-3));

        var service = new CacheEvictionService(store, Options.Create(new MirrorOptions
        {
            Cache = new CacheOptions
            {
                Enabled = false,
                Eviction = new CacheEvictionOptions { Enabled = true, MaxAge = TimeSpan.FromHours(1) },
            },
        }), NullLogger<CacheEvictionService>.Instance, new MirrorMetrics(), clock);

        await service.StartAsync(TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromDays(1));
        await service.StopAsync(TestContext.Current.CancellationToken);

        Assert.Empty(store.DeletedKeys);
    }

    [Fact]
    public async Task ExecuteAsync_Continues_WhenSweepThrows()
    {
        var store = new ThrowingMaintenanceStore(new InvalidOperationException("Enumerate failed"), throwOnCall: 1);

        var options = new CacheEvictionOptions
        {
            Enabled = true,
            MaxAge = null,
            MaxSizeBytes = null,
        };

        var service = new CacheEvictionService(store, Options.Create(new MirrorOptions
        {
            Cache = new CacheOptions
            {
                Enabled = true,
                Eviction = options,
            },
        }), NullLogger<CacheEvictionService>.Instance, new MirrorMetrics(), TimeProvider.System);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await store.EnumerationCompleted.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, store.EnumerateCalls);
        await service.StopAsync(TestContext.Current.CancellationToken);

        Assert.True(service.ExecuteTask?.IsCompleted == true);
    }

    [Fact]
    public async Task ExecuteAsync_Continues_WhenStatsPassThrows()
    {
        var store = new ThrowingMaintenanceStore(new InvalidOperationException("Enumerate failed"), throwOnCall: 1);

        var service = new CacheEvictionService(store, Options.Create(new MirrorOptions
        {
            Cache = new CacheOptions
            {
                Enabled = true,
                Eviction = new CacheEvictionOptions { Enabled = false },
                SizeReporting = new CacheSizeReportingOptions { Enabled = true },
            },
        }), NullLogger<CacheEvictionService>.Instance, new MirrorMetrics(), TimeProvider.System);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await store.EnumerationCompleted.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, store.EnumerateCalls);

        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsCleanly_WhenCancelledMidSweep()
    {
        var store = new BlockingMaintenanceStore();

        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var options = new CacheEvictionOptions
        {
            Enabled = true,
            MaxAge = null,
            Interval = TimeSpan.FromDays(1),
        };

        var service = new CacheEvictionService(store, Options.Create(new MirrorOptions
        {
            Cache = new CacheOptions
            {
                Enabled = true,
                Eviction = options,
            },
        }), NullLogger<CacheEvictionService>.Instance, new MirrorMetrics(), clock);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);

        Assert.True(service.ExecuteTask?.IsCompleted == true);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsCleanly_WhenCancelledMidStatsPass()
    {
        var store = new BlockingMaintenanceStore();

        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var service = new CacheEvictionService(store, Options.Create(new MirrorOptions
        {
            Cache = new CacheOptions
            {
                Enabled = true,
                Eviction = new CacheEvictionOptions { Enabled = false },
                SizeReporting = new CacheSizeReportingOptions { Enabled = true },
            },
        }), NullLogger<CacheEvictionService>.Instance, new MirrorMetrics(), clock);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);

        Assert.True(service.ExecuteTask?.IsCompleted == true);
    }

    [Fact]
    public void GetInterval_UsesEvictionInterval()
    {
        var cache = new CacheOptions
        {
            Enabled = true,
            Eviction = new CacheEvictionOptions
            {
                Enabled = true,
                Interval = TimeSpan.FromSeconds(10),
            },
        };

        Assert.Equal(TimeSpan.FromSeconds(10), CacheEvictionService.GetInterval(cache));
    }

    [Fact]
    public void GetInterval_UsesSizeReportingInterval_WhenEvictionDisabled()
    {
        var cache = new CacheOptions
        {
            Enabled = true,
            Eviction = new CacheEvictionOptions { Enabled = false },
            SizeReporting = new CacheSizeReportingOptions
            {
                Enabled = true,
                Interval = TimeSpan.FromMinutes(5),
            },
        };

        Assert.Equal(TimeSpan.FromMinutes(5), CacheEvictionService.GetInterval(cache));
    }

    [Fact]
    public void GetInterval_UsesEvictionInterval_WhenBothEnabled()
    {
        var cache = new CacheOptions
        {
            Enabled = true,
            Eviction = new CacheEvictionOptions
            {
                Enabled = true,
                Interval = TimeSpan.FromSeconds(10),
            },
            SizeReporting = new CacheSizeReportingOptions
            {
                Enabled = true,
                Interval = TimeSpan.FromMinutes(5),
            },
        };

        Assert.Equal(TimeSpan.FromSeconds(10), CacheEvictionService.GetInterval(cache));
    }

    [Fact]
    public void GetInterval_FallsBackToFifteenMinutes_WhenIntervalZero()
    {
        var cache = new CacheOptions
        {
            Enabled = true,
            Eviction = new CacheEvictionOptions
            {
                Enabled = true,
                Interval = TimeSpan.Zero,
            },
        };

        Assert.Equal(TimeSpan.FromMinutes(15), CacheEvictionService.GetInterval(cache));
    }

    [Fact]
    public async Task ExecuteAsync_Continues_WhenDeleteFails()
    {
        var store = new FakeErroringStore();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        DateTimeOffset oldTime = clock.GetUtcNow().AddHours(-2);
        store.AddEntry("pkg.a/1.0.0/pkg.a.nupkg", 1000, oldTime);
        store.AddEntry("pkg.b/1.0.0/pkg.b.nupkg", 1000, oldTime);

        var options = new CacheEvictionOptions
        {
            Enabled = true,
            MaxAge = TimeSpan.FromHours(1),
            Interval = TimeSpan.FromDays(1),
        };

        var stats = new CacheStatsState(TimeProvider.System);
        var metrics = new MirrorMetrics(stats);
        var service = new CacheEvictionService(store, Options.Create(new MirrorOptions
        {
            Cache = new CacheOptions
            {
                Enabled = true,
                Eviction = options,
            },
        }), NullLogger<CacheEvictionService>.Instance, metrics, clock);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await store.EnumerationCompleted.Task.WaitAsync(TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, stats.EntryCount);
    }
}
