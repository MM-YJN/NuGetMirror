using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using NuGetMirror.Configuration;
using NuGetMirror.Storage;

namespace NuGetMirror.UnitTests;

public sealed class FileSystemPackageContentStoreTests : IDisposable
{
    private readonly string _cacheDir;

    public FileSystemPackageContentStoreTests()
        => _cacheDir = Path.Join(Path.GetTempPath(), "nuget-mirror-test-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task TryGetAsync_ReturnsNull_WhenFileDoesNotExist()
    {
        FileSystemPackageContentStore store = CreateStore();

        CachedContent? result = await store.TryGetAsync("nonexistent/1.0.0/nonexistent.nupkg", TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task WriteAndRead_Success()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        FileSystemPackageContentStore store = CreateStore();
        string key = "test.package/1.0.0/test.package.1.0.0.nupkg";
        byte[] content = "fake-nupkg-content"u8.ToArray();

        await using (ICacheWriteHandle handle = await store.BeginWriteAsync(key, ct))
        {
            handle.SetMetadata("application/octet-stream", content.Length, null);
            await handle.Stream.WriteAsync(content, ct);
            await handle.CommitAsync(ct);
        }

        CachedContent? hit = await store.TryGetAsync(key, ct);
        Assert.NotNull(hit);
        Assert.Equal(content.Length, hit.Length);
        Assert.Equal("application/octet-stream", hit.ContentType);

        byte[] buffer = new byte[content.Length];
        int read = await hit.Stream.ReadAsync(buffer, ct);
        Assert.Equal(content.Length, read);
        Assert.Equal(content, buffer);

        await hit.Stream.DisposeAsync();
    }

    [Fact]
    public async Task AbortedWrite_DoesNotLeaveFile()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        FileSystemPackageContentStore store = CreateStore();
        string key = "test.package/2.0.0/test.package.2.0.0.nupkg";

        ICacheWriteHandle handle = await store.BeginWriteAsync(key, ct);
        handle.SetMetadata("application/octet-stream", 10, null);
        await handle.Stream.WriteAsync("incomplete"u8.ToArray(), ct);
        await handle.DisposeAsync();

        CachedContent? hit = await store.TryGetAsync(key, ct);
        Assert.Null(hit);
    }

    [Fact]
    public async Task WriteThenRead_Nuspec_ReturnsCorrectContentType()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        FileSystemPackageContentStore store = CreateStore();
        string key = "test.package/1.0.0/test.package.nuspec";
        byte[] content = "<package/>"u8.ToArray();

        await using (ICacheWriteHandle handle = await store.BeginWriteAsync(key, ct))
        {
            handle.SetMetadata("application/xml", content.Length, null);
            await handle.Stream.WriteAsync(content, ct);
            await handle.CommitAsync(ct);
        }

        CachedContent? hit = await store.TryGetAsync(key, ct);
        Assert.NotNull(hit);
        Assert.Equal("application/xml", hit.ContentType);
        await hit.Stream.DisposeAsync();
    }

    [Fact]
    public async Task MetaFile_StoresContentTypeAndLength()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        FileSystemPackageContentStore store = CreateStore();
        string key = "test.package/1.0.0/test.package.1.0.0.nupkg";
        byte[] content = "hello-world"u8.ToArray();

        await using (ICacheWriteHandle handle = await store.BeginWriteAsync(key, ct))
        {
            handle.SetMetadata("application/octet-stream", 11, null);
            await handle.Stream.WriteAsync(content, ct);
            await handle.CommitAsync(ct);
        }

        string contentPath = Path.Join(_cacheDir, "test.package", "1.0.0", "test.package.1.0.0.nupkg");
        string metaPath = contentPath + ".meta";
        Assert.True(File.Exists(metaPath));

        string[] metaLines = await File.ReadAllLinesAsync(metaPath, ct);
        Assert.Contains("length=11", metaLines);
        Assert.Contains("content-type=application/octet-stream", metaLines);
        Assert.Contains(metaLines, line => line.StartsWith("fetched-at=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WriteAndRead_StoredAtUtc_IsSet()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        FileSystemPackageContentStore store = CreateStore(clock);
        string key = "test.package/1.0.0/test.package.1.0.0.nupkg";
        byte[] content = "hello-world"u8.ToArray();
        DateTimeOffset beforeWrite = clock.GetUtcNow();

        await using (ICacheWriteHandle handle = await store.BeginWriteAsync(key, ct))
        {
            handle.SetMetadata("application/octet-stream", content.Length, "\"etag-value\"");
            await handle.Stream.WriteAsync(content, ct);
            await handle.CommitAsync(ct);
        }

        CachedContent? hit = await store.TryGetAsync(key, ct);
        Assert.NotNull(hit);
        Assert.NotNull(hit.StoredAtUtc);
        Assert.InRange(hit.StoredAtUtc.Value, beforeWrite.AddSeconds(-5), clock.GetUtcNow().AddSeconds(5));
        Assert.Equal("\"etag-value\"", hit.ETag);

        await hit.Stream.DisposeAsync();
    }

    [Fact]
    public async Task RefreshTimestampAsync_UpdatesStoredAtUtc_PreservesContent()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        FileSystemPackageContentStore store = CreateStore();
        string key = "test.package/1.0.0/test.package.1.0.0.nupkg";
        byte[] content = "original-content"u8.ToArray();

        await using (ICacheWriteHandle handle = await store.BeginWriteAsync(key, ct))
        {
            handle.SetMetadata("application/octet-stream", content.Length, "\"etag-1\"");
            await handle.Stream.WriteAsync(content, ct);
            await handle.CommitAsync(ct);
        }

        CachedContent? firstRead = await store.TryGetAsync(key, ct);
        Assert.NotNull(firstRead);
        Assert.NotNull(firstRead.StoredAtUtc);
        DateTimeOffset firstTimestamp = firstRead.StoredAtUtc.Value;
        await firstRead.Stream.DisposeAsync();

        // Wait to ensure a measurable time difference.
        await Task.Delay(50, ct);

        DateTimeOffset newTimestamp = DateTimeOffset.UtcNow;
        await store.RefreshTimestampAsync(key, "application/octet-stream", "\"etag-2\"", newTimestamp, ct);

        CachedContent? secondRead = await store.TryGetAsync(key, ct);
        Assert.NotNull(secondRead);
        Assert.NotNull(secondRead.StoredAtUtc);
        Assert.True(secondRead.StoredAtUtc.Value > firstTimestamp, "StoredAtUtc should be updated.");
        Assert.Equal("\"etag-2\"", secondRead.ETag);

        // Content must be unchanged.
        byte[] buffer = new byte[content.Length];
        int read = await secondRead.Stream.ReadAsync(buffer, ct);
        Assert.Equal(content.Length, read);
        Assert.Equal(content, buffer);

        await secondRead.Stream.DisposeAsync();
    }

    [Fact]
    public async Task RefreshTimestampAsync_NoExistingContent_DoesNotThrow()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        FileSystemPackageContentStore store = CreateStore();

        // Calling RefreshTimestampAsync on a non-existent entry should not throw.
        await store.RefreshTimestampAsync("nonexistent/1.0.0/file.nupkg", "application/octet-stream", null, DateTimeOffset.UtcNow, ct);

        CachedContent? hit = await store.TryGetAsync("nonexistent/1.0.0/file.nupkg", ct);
        Assert.Null(hit);  // Still not created
    }

    [Fact]
    public async Task ConsecutiveCommits_OverwriteExistingFile()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        FileSystemPackageContentStore store = CreateStore();
        string key = "test.package/1.0.0/test.package.1.0.0.nupkg";
        byte[] content1 = "first-version"u8.ToArray();
        byte[] content2 = "second-version-with-more-data"u8.ToArray();

        await using (ICacheWriteHandle handle1 = await store.BeginWriteAsync(key, ct))
        {
            handle1.SetMetadata("application/octet-stream", content1.Length, null);
            await handle1.Stream.WriteAsync(content1, ct);
            await handle1.CommitAsync(ct);
        }

        await using (ICacheWriteHandle handle2 = await store.BeginWriteAsync(key, ct))
        {
            handle2.SetMetadata("application/octet-stream", content2.Length, null);
            await handle2.Stream.WriteAsync(content2, ct);
            await handle2.CommitAsync(ct);
        }
        CachedContent? hit = await store.TryGetAsync(key, ct);

        Assert.NotNull(hit);
        Assert.Equal(content2.Length, hit.Length);
        await hit.Stream.DisposeAsync();
    }

    [Fact]
    public async Task TryGetAsync_FallsBackToFileInfo_WhenMetaLengthIsCorrupt()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string key = "test.package/1.0.0/test.package.1.0.0.nupkg";
        byte[] content = "fake-corrupt-meta-nupkg"u8.ToArray();
        string contentPath = Path.Join(_cacheDir, "test.package", "1.0.0", "test.package.1.0.0.nupkg");
        string metaPath = contentPath + ".meta";

        Directory.CreateDirectory(Path.GetDirectoryName(contentPath)
            ?? throw new InvalidOperationException($"Could not determine directory from '{contentPath}'."));
        await File.WriteAllBytesAsync(contentPath, content, ct);
        await File.WriteAllTextAsync(metaPath, "length=abc\ncontent-type=application/octet-stream\n", ct);

        FileSystemPackageContentStore store = CreateStore();
        CachedContent? hit = await store.TryGetAsync(key, ct);

        Assert.NotNull(hit);
        Assert.Equal(content.Length, hit.Length);
        Assert.Equal("application/octet-stream", hit.ContentType);

        byte[] buffer = new byte[content.Length];
        _ = await hit.Stream.ReadAsync(buffer, ct);
        Assert.Equal(content, buffer);
        await hit.Stream.DisposeAsync();
    }

    [Fact]
    public async Task TryGetAsync_FallsBackToFileInfo_WhenMetaHasNoLength()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string key = "test.package/1.0.0/test.package.1.0.0.nupkg";
        byte[] content = "fake-nolength-meta"u8.ToArray();
        string contentPath = Path.Join(_cacheDir, "test.package", "1.0.0", "test.package.1.0.0.nupkg");
        string metaPath = contentPath + ".meta";

        Directory.CreateDirectory(Path.GetDirectoryName(contentPath)
            ?? throw new InvalidOperationException($"Could not determine directory from '{contentPath}'."));
        await File.WriteAllBytesAsync(contentPath, content, ct);
        await File.WriteAllTextAsync(metaPath, "content-type=application/octet-stream\n", ct);

        FileSystemPackageContentStore store = CreateStore();
        CachedContent? hit = await store.TryGetAsync(key, ct);

        Assert.NotNull(hit);
        Assert.Equal(content.Length, hit.Length);
        Assert.Equal("application/octet-stream", hit.ContentType);

        byte[] buffer = new byte[content.Length];
        _ = await hit.Stream.ReadAsync(buffer, ct);
        Assert.Equal(content, buffer);
        await hit.Stream.DisposeAsync();
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
