using System.Diagnostics;

using Microsoft.Extensions.Options;

using NuGetMirror.Configuration;
using NuGetMirror.Diagnostics;

namespace NuGetMirror.Storage;

internal sealed partial class CacheEvictionService(
    IPackageContentStore store,
    IOptions<MirrorOptions> options,
    ILogger<CacheEvictionService> logger,
    MirrorMetrics metrics,
    TimeProvider timeProvider
    ) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            CacheEvictionOptions eviction = options.Value.Cache.Eviction;

            if (eviction.Enabled && options.Value.Cache.Enabled && store is ICacheMaintenance maintenance)
            {
                try
                {
                    await SweepAsync(maintenance, eviction, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    LogSweepFailed(ex);
                }
            }
            else if (options.Value.Cache.SizeReporting.Enabled
                     && options.Value.Cache.Enabled
                     && store is ICacheMaintenance reportingMaintenance)
            {
                try
                {
                    await EnumerateStatsOnlyAsync(reportingMaintenance, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    LogStatsPassFailed(ex);
                }
            }

            TimeSpan interval = GetInterval(options.Value.Cache);

            try
            {
                await Task.Delay(interval, timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task SweepAsync(ICacheMaintenance maintenance, CacheEvictionOptions eviction, CancellationToken ct)
    {
        LogSweepStarting();
        long startTime = Stopwatch.GetTimestamp();
        // EvictionStrategy (Oldest/Lru) is realized via TouchAsync in the request path,
        // not branched here. The sweep always orders by LastModifiedUtc. Under Lru, cache
        // hits refresh that timestamp via touch, making it effectively a last-access time.
        // Eviction targets all cached content — package files, readmes, vulnerability data,
        // and repository-signature data — not just .nupkg/.nuspec.
        var entries = new List<CacheEntryInfo>();

        await foreach (CacheEntryInfo entry in maintenance.EnumerateAsync(ct).ConfigureAwait(false))
        {
            entries.Add(entry);
        }

        if (entries.Count == 0)
        {
            LogSweepEmpty();
            metrics.RecordSweepDuration(Stopwatch.GetElapsedTime(startTime).TotalSeconds);
            metrics.UpdateCacheSize(0, 0);
            return;
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        int deleted = 0;
        long freedBytes = 0L;
        int remainingCount = entries.Count;
        long remainingTotal = 0L;

        if (eviction.MaxAge is { } maxAge)
        {
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                CacheEntryInfo entry = entries[i];

                if (now - entry.LastModifiedUtc > maxAge)
                {
                    try
                    {
                        await maintenance.DeleteAsync(entry.Key, ct).ConfigureAwait(false);
                        deleted++;
                        freedBytes += entry.Length;
                        entries.RemoveAt(i);
                        metrics.RecordEviction("max_age");
                    }
                    catch (Exception ex)
                    {
                        LogDeleteFailed(ex);
                    }
                }
            }
        }

        remainingCount = entries.Count;
        remainingTotal = entries.Sum(e => e.Length);

        if (eviction.MaxSizeBytes is { } maxSize)
        {
            if (remainingTotal > maxSize)
            {
                entries.Sort(static (a, b) => a.LastModifiedUtc.CompareTo(b.LastModifiedUtc));

                long targetSize = (long)(maxSize * eviction.TargetUtilization);

                foreach (CacheEntryInfo entry in entries)
                {
                    if (remainingTotal <= targetSize)
                    {
                        break;
                    }

                    try
                    {
                        await maintenance.DeleteAsync(entry.Key, ct).ConfigureAwait(false);
                        deleted++;
                        freedBytes += entry.Length;
                        remainingTotal -= entry.Length;
                        remainingCount--;
                        metrics.RecordEviction("max_size");
                    }
                    catch (Exception ex)
                    {
                        LogDeleteFailed(ex);
                    }
                }
            }
        }

        if (deleted > 0)
        {
            LogSweepCompleted(deleted, freedBytes);
            metrics.RecordEvictedBytes(freedBytes);
        }

        metrics.RecordSweepDuration(Stopwatch.GetElapsedTime(startTime).TotalSeconds);
        metrics.UpdateCacheSize(remainingTotal, remainingCount);
    }

    private async Task EnumerateStatsOnlyAsync(ICacheMaintenance maintenance, CancellationToken ct)
    {
        var entries = new List<CacheEntryInfo>();

        await foreach (CacheEntryInfo entry in maintenance.EnumerateAsync(ct).ConfigureAwait(false))
        {
            entries.Add(entry);
        }

        if (entries.Count == 0)
        {
            metrics.UpdateCacheSize(0, 0);
            return;
        }

        long totalBytes = entries.Sum(e => e.Length);
        metrics.UpdateCacheSize(totalBytes, entries.Count);
    }

    internal static TimeSpan GetInterval(CacheOptions cache)
    {
        CacheEvictionOptions eviction = cache.Eviction;
        return cache.SizeReporting.Enabled && !eviction.Enabled
            && cache.SizeReporting.Interval > TimeSpan.Zero
            ? cache.SizeReporting.Interval
            : eviction.Interval > TimeSpan.Zero ? eviction.Interval : TimeSpan.FromMinutes(15);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cache eviction sweep failed.")]
    private partial void LogSweepFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Starting cache eviction sweep.")]
    private partial void LogSweepStarting();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Cache eviction sweep found no entries.")]
    private partial void LogSweepEmpty();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cache stats enumeration failed.")]
    private partial void LogStatsPassFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Cache eviction sweep completed: {Count} entries evicted ({Freed} bytes freed).")]
    private partial void LogSweepCompleted(int count, long freed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to delete entry during cache eviction sweep.")]
    private partial void LogDeleteFailed(Exception ex);
}
