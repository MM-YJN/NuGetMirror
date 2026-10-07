using Microsoft.Extensions.Options;

using NuGetMirror.Configuration;

namespace NuGetMirror.Storage;

internal sealed partial class FileSystemCachePreflightService(
    IOptions<MirrorOptions> options,
    ILogger<FileSystemCachePreflightService> logger) : IHostedLifecycleService
{
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        MirrorOptions mirrorOptions = options.Value;

        if (!mirrorOptions.Cache.Enabled
            || !string.Equals(mirrorOptions.Cache.Backend, "FileSystem", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string root = Path.GetFullPath(mirrorOptions.Cache.FileSystem.Directory);

        try
        {
            Directory.CreateDirectory(root);
        }
        catch (IOException ex)
        {
            LogCouldNotCreateDirectory(root, ex);
            throw new InvalidOperationException(BuildErrorMessage(root, "could not be created."), ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            LogAndThrowAccessDenied(root, ex);
        }

        string probePath = Path.Join(root, ".cache-probe." + Guid.NewGuid().ToString("N"));

        try
        {
            await File.WriteAllTextAsync(probePath, "ok", cancellationToken).ConfigureAwait(false);
            File.Delete(probePath);
        }
        catch (IOException ex)
        {
            LogDirectoryNotUsable(root, ex);
            throw new InvalidOperationException(BuildErrorMessage(root, "write test failed: " + ex.Message), ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            LogAndThrowAccessDenied(root, ex);
        }

        LogDirectoryWritable(root);
    }

    private void LogAndThrowAccessDenied(string root, Exception ex)
    {
        LogAccessDenied(root, ex);
        throw new InvalidOperationException(BuildErrorMessage(root, "is not writable."), ex);
    }

    private static string BuildErrorMessage(string root, string suffix)
    {
        return $"Cache directory '{root}' {suffix} "
            + "The container runs as a non-root user (default UID 1654). "
            + "If you mounted a Docker volume at this path it is likely owned by root. "
            + "Fix with one of: run the container with --user to match the directory owner, "
            + "chown the volume to the app user, choose a different Mirror:Cache:FileSystem:Directory, "
            + "or disable caching with Mirror:Cache:Enabled=false.";
    }

    [LoggerMessage(LogLevel.Error, "Cache directory '{Root}' could not be created.")]
    private partial void LogCouldNotCreateDirectory(string root, Exception ex);

    [LoggerMessage(LogLevel.Error, "Cache directory '{Root}' is not usable for writing.")]
    private partial void LogDirectoryNotUsable(string root, Exception ex);

    [LoggerMessage(LogLevel.Error, "Cache directory '{Root}' is not writable.")]
    private partial void LogAccessDenied(string root, Exception ex);

    [LoggerMessage(LogLevel.Information, "Cache directory '{Root}' is writable.")]
    private partial void LogDirectoryWritable(string root);

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
