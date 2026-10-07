using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NuGetMirror.Configuration;
using NuGetMirror.Diagnostics.HealthChecks;
using NuGetMirror.Storage;

namespace NuGetMirror.UnitTests;

public sealed class StorageHealthCheckTests : IAsyncDisposable
{
    private string? _tempDir;

    [Fact]
    public async Task ReturnsHealthy_WhenCachingDisabled()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var options = new MirrorOptions { Cache = new CacheOptions { Enabled = false } };
        IOptions<MirrorOptions> optionsWrapper = Microsoft.Extensions.Options.Options.Create(options);
        var store = new StorageWithoutProbe();
        var check = new StorageHealthCheck(store, optionsWrapper, NullLogger<StorageHealthCheck>.Instance);

        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext(), ct);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task ReturnsHealthy_WhenFileSystemDirectoryExists()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        _tempDir = Path.Join(Path.GetTempPath(), "healthcheck-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var options = new MirrorOptions
        {
            Cache = new CacheOptions
            {
                Enabled = true,
                Backend = "FileSystem",
                FileSystem = new FileSystemOptions { Directory = _tempDir },
            },
        };
        IOptions<MirrorOptions> optionsWrapper = Microsoft.Extensions.Options.Options.Create(options);
        var store = new FileSystemPackageContentStore(optionsWrapper, NullLogger<FileSystemPackageContentStore>.Instance, TimeProvider.System);
        var check = new StorageHealthCheck(store, optionsWrapper, NullLogger<StorageHealthCheck>.Instance);

        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext(), ct);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task ReturnsUnhealthy_WhenFileSystemDirectoryMissing()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string missingDir = Path.Join(Path.GetTempPath(), "healthcheck-missing-" + Guid.NewGuid().ToString("N"));
        var options = new MirrorOptions
        {
            Cache = new CacheOptions
            {
                Enabled = true,
                Backend = "FileSystem",
                FileSystem = new FileSystemOptions { Directory = missingDir },
            },
        };
        IOptions<MirrorOptions> optionsWrapper = Microsoft.Extensions.Options.Options.Create(options);
        var store = new FileSystemPackageContentStore(optionsWrapper, NullLogger<FileSystemPackageContentStore>.Instance, TimeProvider.System);
        var check = new StorageHealthCheck(store, optionsWrapper, NullLogger<StorageHealthCheck>.Instance);

        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext(), ct);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task ReturnsHealthy_WhenStoreDoesNotImplementProbe()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var options = new MirrorOptions
        {
            Cache = new CacheOptions { Enabled = true },
        };
        IOptions<MirrorOptions> optionsWrapper = Microsoft.Extensions.Options.Options.Create(options);
        var store = new StorageWithoutProbe();
        var check = new StorageHealthCheck(store, optionsWrapper, NullLogger<StorageHealthCheck>.Instance);

        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext(), ct);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    public async ValueTask DisposeAsync()
    {
        if (_tempDir is not null && Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }

        await ValueTask.CompletedTask;
    }

    private sealed class StorageWithoutProbe : IPackageContentStore
    {
        public ValueTask<CachedContent?> TryGetAsync(string key, CancellationToken ct)
            => ValueTask.FromResult<CachedContent?>(null);

        public ValueTask<ICacheWriteHandle> BeginWriteAsync(string key, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
