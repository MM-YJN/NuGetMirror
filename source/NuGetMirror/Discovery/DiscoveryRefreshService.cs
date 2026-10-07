using Microsoft.Extensions.Options;

using NuGetMirror.Configuration;

namespace NuGetMirror.Discovery;

internal sealed partial class DiscoveryRefreshService(
    DiscoveryCache cache,
    IOptions<MirrorOptions> options,
    ILogger<DiscoveryRefreshService> logger
    ) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Upstream.BackgroundRefresh)
        {
            return;
        }

        try
        {
            await cache.RefreshAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogInitialRefreshFailed(ex);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan ttl = options.Value.Upstream.DiscoveryCacheTtl;
            TimeSpan? customInterval = options.Value.Upstream.DiscoveryRefreshInterval;
            TimeSpan interval = customInterval ?? TimeSpan.FromTicks(Math.Max(ttl.Ticks - TimeSpan.FromMinutes(1).Ticks, TimeSpan.FromMinutes(1).Ticks));

            try
            {
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                await cache.RefreshAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogPeriodicRefreshFailed(ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Initial discovery refresh failed.")]
    private partial void LogInitialRefreshFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Periodic discovery refresh failed.")]
    private partial void LogPeriodicRefreshFailed(Exception ex);
}
