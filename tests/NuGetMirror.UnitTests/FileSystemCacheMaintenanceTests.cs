using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using NuGetMirror.Configuration;
using NuGetMirror.Storage;

namespace NuGetMirror.UnitTests;

public sealed class FileSystemCacheMaintenanceTests : IDisposable
{
    private readonly string _cacheDir;

    public FileSystemCacheMaintenanceTests()
        => _cacheDir = Path.Join(Path.GetTempPath(), "nuget-mirror-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task EnumerateAsync_ReturnsEntries_ForNupkgAndNuspec()
    {
        FileSystemPackageContentStore store = CreateStore();
        await WriteFileAsync(store, "package.a/1.0.0/package.a.1.0.0.nupkg", "nupkg-content"u8.ToArray());
        await WriteFileAsync(store, "package.a/1.0.0/package.a.nuspec", "nuspec-content"u8.ToArray());

        List<CacheEntryInfo> entries = await EnumerateAsync(store);

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.Key == "package.a/1.0.0/package.a.1.0.0.nupkg" && e.Length == 13);
        Assert.Contains(entries, e => e.Key == "package.a/1.0.0/package.a.nuspec" && e.Length == 14);
    }

    [Fact]
    public async Task EnumerateAsync_SkipsMetaFiles()
    {
        FileSystemPackageContentStore store = CreateStore();
        await WriteFileAsync(store, "package.a/1.0.0/package.a.1.0.0.nupkg", "content"u8.ToArray());

        string metaPath = Path.Join(_cacheDir, "package.a", "1.0.0", "package.a.1.0.0.nupkg.meta");
        Directory.CreateDirectory(Path.GetDirectoryName(metaPath)
            ?? throw new InvalidOperationException($"Could not determine directory from '{metaPath}'."));
        await File.WriteAllTextAsync(metaPath, "length=7", TestContext.Current.CancellationToken);

        List<CacheEntryInfo> entries = await EnumerateAsync(store);

        Assert.Single(entries.Select(e => e.Key));
    }

    [Fact]
    public async Task EnumerateAsync_SkipsTempFiles()
    {
        FileSystemPackageContentStore store = CreateStore();
        await WriteFileAsync(store, "package.a/1.0.0/package.a.1.0.0.nupkg", "content"u8.ToArray());

        string tempPath = Path.Join(_cacheDir, "package.a", "1.0.0", "package.a.1.0.0.nupkg.tmp.abc123");
        Directory.CreateDirectory(Path.GetDirectoryName(tempPath)
            ?? throw new InvalidOperationException($"Could not determine directory from '{tempPath}'."));
        await File.WriteAllTextAsync(tempPath, "temp", TestContext.Current.CancellationToken);

        List<CacheEntryInfo> entries = await EnumerateAsync(store);

        Assert.Single(entries.Select(e => e.Key));
    }

    [Fact]
    public async Task EnumerateAsync_IncludesPackageIdContainingTmp()
    {
        FileSystemPackageContentStore store = CreateStore();
        await WriteFileAsync(store, "my.tmp.package/1.0.0/my.tmp.package.1.0.0.nupkg", "content"u8.ToArray());

        List<CacheEntryInfo> entries = await EnumerateAsync(store);

        Assert.Single(entries);
        Assert.EndsWith(".nupkg", entries[0].Key, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnumerateAsync_LastModifiedUtc_IsSet()
    {
        FileSystemPackageContentStore store = CreateStore();
        await WriteFileAsync(store, "package.a/1.0.0/package.a.1.0.0.nupkg", "content"u8.ToArray());

        List<CacheEntryInfo> entries = await EnumerateAsync(store);

        Assert.Single(entries);
        Assert.True(entries[0].LastModifiedUtc > DateTimeOffset.MinValue);
        Assert.True(entries[0].LastModifiedUtc <= DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task DeleteAsync_RemovesContentAndMetaFile()
    {
        FileSystemPackageContentStore store = CreateStore();
        string key = "package.a/1.0.0/package.a.1.0.0.nupkg";
        await WriteFileAsync(store, key, "content"u8.ToArray());

        string contentPath = Path.Join(_cacheDir, "package.a", "1.0.0", "package.a.1.0.0.nupkg");
        string metaPath = contentPath + ".meta";
        Assert.True(File.Exists(contentPath));
        Assert.True(File.Exists(metaPath));

        await store.DeleteAsync(key, TestContext.Current.CancellationToken);

        Assert.False(File.Exists(contentPath));
        Assert.False(File.Exists(metaPath));
    }

    [Fact]
    public async Task DeleteAsync_PrunesEmptyParentDirectories()
    {
        FileSystemPackageContentStore store = CreateStore();
        string key = "package.b/1.0.0/package.b.1.0.0.nupkg";
        await WriteFileAsync(store, key, "content"u8.ToArray());

        string dir = Path.Join(_cacheDir, "package.b", "1.0.0");
        Assert.True(Directory.Exists(dir));

        await store.DeleteAsync(key, TestContext.Current.CancellationToken);

        Assert.False(Directory.Exists(Path.Join(_cacheDir, "package.b")));
        Assert.True(Directory.Exists(_cacheDir));
    }

    [Fact]
    public async Task DeleteAsync_NoOp_ForNonExistentKey()
    {
        FileSystemPackageContentStore store = CreateStore();

        await store.DeleteAsync("nonexistent/1.0.0/nonexistent.nupkg", TestContext.Current.CancellationToken);

        Assert.True(Directory.Exists(_cacheDir) || !Path.Exists(_cacheDir));
    }

    [Fact]
    public async Task DeleteAsync_DoesNotRemoveDirectoriesWithOtherFiles()
    {
        FileSystemPackageContentStore store = CreateStore();
        string key1 = "package.c/1.0.0/package.c.1.0.0.nupkg";
        string key2 = "package.c/2.0.0/package.c.2.0.0.nupkg";
        await WriteFileAsync(store, key1, "content1"u8.ToArray());
        await WriteFileAsync(store, key2, "content2"u8.ToArray());

        await store.DeleteAsync(key1, TestContext.Current.CancellationToken);

        Assert.True(Directory.Exists(Path.Join(_cacheDir, "package.c", "2.0.0")));
        Assert.True(File.Exists(Path.Join(_cacheDir, "package.c", "2.0.0", "package.c.2.0.0.nupkg")));
        Assert.False(Directory.Exists(Path.Join(_cacheDir, "package.c", "1.0.0")));
    }

    [Fact]
    public async Task TouchAsync_UpdatesLastWriteTimeUtc()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        FileSystemPackageContentStore store = CreateStore(clock);
        string key = "package.a/1.0.0/package.a.1.0.0.nupkg";
        await WriteFileAsync(store, key, "content"u8.ToArray());

        string contentPath = Path.Join(_cacheDir, "package.a", "1.0.0", "package.a.1.0.0.nupkg");
        DateTime originalTime = File.GetLastWriteTimeUtc(contentPath);

        clock.Advance(TimeSpan.FromHours(1));
        await store.TouchAsync(key, ct);

        DateTime newTime = File.GetLastWriteTimeUtc(contentPath);
        Assert.True(newTime > originalTime);
    }

    [Fact]
    public async Task TouchAsync_WithFakeClock_BumpsTimestamp_AboveOtherEntries()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        FileSystemPackageContentStore store = CreateStore(clock);

        string keyA = "package.a/1.0.0/package.a.1.0.0.nupkg";
        string keyB = "package.b/1.0.0/package.b.1.0.0.nupkg";

        // Write two entries — both get real filesystem timestamps (~now).
        await WriteFileAsync(store, keyA, "content-a"u8.ToArray());
        await WriteFileAsync(store, keyB, "content-b"u8.ToArray());

        // Advance the fake clock and touch keyA so its LastWriteTimeUtc jumps
        // ahead of keyB — simulating an LRU access that makes keyA most-recent.
        clock.Advance(TimeSpan.FromHours(2));
        await store.TouchAsync(keyA, ct);

        List<CacheEntryInfo> entries = await EnumerateAsync(store);
        CacheEntryInfo entryA = entries.Single(e => e.Key == keyA);
        CacheEntryInfo entryB = entries.Single(e => e.Key == keyB);

        Assert.True(entryA.LastModifiedUtc > entryB.LastModifiedUtc,
            "TouchAsync should bump keyA's timestamp above keyB's, changing eviction order.");
    }

    [Fact]
    public async Task TouchAsync_NoOp_ForNonExistentKey()
    {
        FileSystemPackageContentStore store = CreateStore();

        await store.TouchAsync("nonexistent/1.0.0/nonexistent.nupkg", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task EnumerateAsync_EmptyDirectory_ReturnsEmpty()
    {
        FileSystemPackageContentStore store = CreateStore();
        Directory.CreateDirectory(_cacheDir);

        List<CacheEntryInfo> entries = await EnumerateAsync(store);

        Assert.Empty(entries);
    }

    private FileSystemPackageContentStore CreateStore(TimeProvider? timeProvider = null)
    {
        var mirrorOptions = new MirrorOptions
        {
            Cache = new CacheOptions { FileSystem = new FileSystemOptions { Directory = _cacheDir }, Enabled = true },
        };

        IOptions<MirrorOptions> optionsWrapper = Microsoft.Extensions.Options.Options.Create(mirrorOptions);
        return new FileSystemPackageContentStore(optionsWrapper, NullLogger<FileSystemPackageContentStore>.Instance, timeProvider ?? TimeProvider.System);
    }

    private static async Task WriteFileAsync(FileSystemPackageContentStore store, string key, byte[] content)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using ICacheWriteHandle handle = await store.BeginWriteAsync(key, ct);
        handle.SetMetadata("application/octet-stream", content.Length, null);
        await handle.Stream.WriteAsync(content, ct);
        await handle.CommitAsync(ct);
    }

    private static async Task<List<CacheEntryInfo>> EnumerateAsync(ICacheMaintenance store)
    {
        var entries = new List<CacheEntryInfo>();
        await foreach (CacheEntryInfo entry in store.EnumerateAsync(TestContext.Current.CancellationToken))
        {
            entries.Add(entry);
        }

        return entries;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_cacheDir))
            {
                Directory.Delete(_cacheDir, recursive: true);
            }
        }
        catch
        {
        }
    }
}
