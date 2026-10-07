using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

using NuGetMirror.Configuration;
using NuGetMirror.Discovery;

namespace NuGetMirror.Diagnostics.HealthChecks;

internal sealed partial class UpstreamHealthCheck(
    DiscoveryCache discovery,
    IOptions<MirrorOptions> options,
    ILogger<UpstreamHealthCheck> logger,
    TimeProvider timeProvider) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct)
    {
        try
        {
            DiscoverySnapshot snapshot = await discovery.GetAsync(ct).ConfigureAwait(false);
            TimeSpan ttl = options.Value.Upstream.DiscoveryCacheTtl;
            TimeSpan age = timeProvider.GetUtcNow() - snapshot.FetchedAt;

            if (age <= ttl)
            {
                return HealthCheckResult.Healthy("Upstream index is reachable and fresh.");
            }

            LogStaleSnapshot(age.TotalSeconds);
            return HealthCheckResult.Degraded($"Serving stale upstream index (age: {age.TotalSeconds:F0}s, ttl: {ttl.TotalSeconds:F0}s).");
        }
        catch (Exception ex)
        {
            LogUnavailable(ex);
            return HealthCheckResult.Unhealthy("Upstream index is unavailable and no cached snapshot exists.", ex);
        }
    }

    [LoggerMessage(LogLevel.Warning, "Serving stale upstream index snapshot (age: {AgeSeconds:F0}s).")]
    private partial void LogStaleSnapshot(double ageSeconds);

    [LoggerMessage(LogLevel.Error, "Upstream index unavailable and no cached snapshot.")]
    private partial void LogUnavailable(Exception ex);
}
