using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NuGetMirror.Configuration;
using NuGetMirror.Storage;

namespace NuGetMirror.UnitTests;

public sealed class FileSystemCachePreflightServiceTests : IDisposable
{
    private string? _tempDir;

    [Fact]
    public async Task StartingAsync_NoOp_WhenCacheDisabled()
    {
        var mirrorOptions = new MirrorOptions
        {
            Cache = new CacheOptions
            {
                Enabled = false,
                Backend = "FileSystem",
                FileSystem = new FileSystemOptions { Directory = "/no/such/path" },
            },
        };

        FileSystemCachePreflightService service = CreateService(mirrorOptions);

        await service.StartingAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StartingAsync_NoOp_WhenBackendIsS3()
    {
        var mirrorOptions = new MirrorOptions
        {
            Cache = new CacheOptions
            {
                Enabled = true,
                Backend = "S3",
                FileSystem = new FileSystemOptions { Directory = "/no/such/path" },
            },
        };

        FileSystemCachePreflightService service = CreateService(mirrorOptions);

        await service.StartingAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StartingAsync_Succeeds_WhenDirectoryIsWritable()
    {
        string dir = CreateTempDir();
        var mirrorOptions = new MirrorOptions
        {
            Cache = new CacheOptions
            {
                Enabled = true,
                Backend = "FileSystem",
                FileSystem = new FileSystemOptions { Directory = dir },
            },
        };

        FileSystemCachePreflightService service = CreateService(mirrorOptions);

        await service.StartingAsync(TestContext.Current.CancellationToken);

        Assert.True(Directory.Exists(dir));
    }

    [Fact]
    public async Task StartingAsync_Throws_WhenDirectoryIsNotWritable()
    {
        string dir = CreateTempDir();

        // Make the directory read-only on supported platforms
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            UnixFileMode noWrite = UnixFileMode.UserRead | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
            File.SetUnixFileMode(dir, noWrite);
        }
        else if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(dir, FileAttributes.ReadOnly);
        }
        else
        {
            // Platform not supported, skip the write-failure test
            return;
        }

        var mirrorOptions = new MirrorOptions
        {
            Cache = new CacheOptions
            {
                Enabled = true,
                Backend = "FileSystem",
                FileSystem = new FileSystemOptions { Directory = dir },
            },
        };

        FileSystemCachePreflightService service = CreateService(mirrorOptions);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartingAsync(TestContext.Current.CancellationToken));
        Assert.Contains("not writable", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartingAsync_Throws_WhenDirectoryPathIsAFile()
    {
        string dir = CreateTempDir();
        string filePath = Path.Join(dir, "not-a-dir");
        await File.WriteAllTextAsync(filePath, "block", TestContext.Current.CancellationToken);

        var mirrorOptions = new MirrorOptions
        {
            Cache = new CacheOptions
            {
                Enabled = true,
                Backend = "FileSystem",
                FileSystem = new FileSystemOptions { Directory = filePath },
            },
        };

        FileSystemCachePreflightService service = CreateService(mirrorOptions);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartingAsync(TestContext.Current.CancellationToken));
        Assert.Contains("could not be created", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartingAsync_CreatesDirectory_WhenItDoesNotExist()
    {
        string parent = CreateTempDir();
        string childDir = Path.Join(parent, "nested", "cache");

        var mirrorOptions = new MirrorOptions
        {
            Cache = new CacheOptions
            {
                Enabled = true,
                Backend = "FileSystem",
                FileSystem = new FileSystemOptions { Directory = childDir },
            },
        };

        FileSystemCachePreflightService service = CreateService(mirrorOptions);

        await service.StartingAsync(TestContext.Current.CancellationToken);

        Assert.True(Directory.Exists(childDir));
    }

    private string CreateTempDir()
    {
        string dir = Path.Join(Path.GetTempPath(), "nuget-mirror-preflight-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDir = dir;
        return dir;
    }

    private static FileSystemCachePreflightService CreateService(MirrorOptions mirrorOptions)
    {
        IOptions<MirrorOptions> optionsWrapper = Microsoft.Extensions.Options.Options.Create(mirrorOptions);
        return new FileSystemCachePreflightService(optionsWrapper, NullLogger<FileSystemCachePreflightService>.Instance);
    }

    public void Dispose()
    {
        if (_tempDir is not null && Directory.Exists(_tempDir))
        {
            try
            {
                // Restore write permissions so we can delete recursively
                if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                {
                    File.SetUnixFileMode(_tempDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
                else if (OperatingSystem.IsWindows())
                {
                    File.SetAttributes(_tempDir, FileAttributes.Normal);
                }

                Directory.Delete(_tempDir, recursive: true);
            }
            catch
            {
            }
        }
    }
}
