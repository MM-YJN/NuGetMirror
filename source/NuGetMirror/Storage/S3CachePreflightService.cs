using Microsoft.Extensions.Options;

using NuGetMirror.Configuration;
using NuGetMirror.Storage.S3;

namespace NuGetMirror.Storage;

internal sealed partial class S3CachePreflightService(
    IOptions<MirrorOptions> options,
    S3Client client,
    ILogger<S3CachePreflightService> logger) : IHostedLifecycleService
{
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        MirrorOptions mirrorOptions = options.Value;

        if (!mirrorOptions.Cache.Enabled
            || !string.Equals(mirrorOptions.Cache.Backend, "S3", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        S3Options s3 = mirrorOptions.Cache.S3;

        if (string.IsNullOrEmpty(s3.Bucket))
        {
            throw new InvalidOperationException(
                "Mirror:Cache:S3:Bucket is required when Backend is 'S3'.");
        }

        try
        {
            await client.CheckAsync(cancellationToken).ConfigureAwait(false);
            LogProbeSucceeded(s3.Bucket);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"S3 bucket '{s3.Bucket}' is not reachable. "
                + "Verify your S3 configuration (Bucket, Region, ServiceUrl, AccessKey, SecretKey, UsePathStyle). "
                + $"Error: {ex.Message}", ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "S3 bucket '{Bucket}' is reachable.")]
    private partial void LogProbeSucceeded(string bucket);

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
