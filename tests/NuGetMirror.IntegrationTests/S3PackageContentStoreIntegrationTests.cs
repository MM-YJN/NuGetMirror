using NuGetMirror.Storage;
using NuGetMirror.Storage.S3;

namespace NuGetMirror.IntegrationTests;

public sealed class S3PackageContentStoreIntegrationTests(MinioFixture fixture) : IClassFixture<MinioFixture>
{
    [Fact(Timeout = 120_000)]
    [Trait("Category", "Integration")]
    public async Task WriteAndRead_Success()
    {
        if (!fixture.DockerAvailable)
        {
            Assert.Skip("Docker is not available");
        }

        CancellationToken ct = TestContext.Current.CancellationToken;
        S3PackageContentStore store = fixture.CreateStore();
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
        await hit.Stream.ReadExactlyAsync(buffer, ct);
        Assert.Equal(content, buffer);

        await hit.Stream.DisposeAsync();
    }

    [Fact(Timeout = 120_000)]
    [Trait("Category", "Integration")]
    public async Task WriteAndRead_Nuspec_ReturnsCorrectContentType()
    {
        if (!fixture.DockerAvailable)
        {
            Assert.Skip("Docker is not available");
        }

        CancellationToken ct = TestContext.Current.CancellationToken;
        S3PackageContentStore store = fixture.CreateStore();
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

    [Fact(Timeout = 120_000)]
    [Trait("Category", "Integration")]
    public async Task WriteAndRead_PreservesETag()
    {
        if (!fixture.DockerAvailable)
        {
            Assert.Skip("Docker is not available");
        }

        CancellationToken ct = TestContext.Current.CancellationToken;
        S3PackageContentStore store = fixture.CreateStore();
        string key = "test.package/1.0.0/test.package.1.0.0.nupkg";
        byte[] content = "hello-etag"u8.ToArray();
        string expectedETag = "\"abc123\"";

        await using (ICacheWriteHandle handle = await store.BeginWriteAsync(key, ct))
        {
            handle.SetMetadata("application/octet-stream", content.Length, expectedETag);
            await handle.Stream.WriteAsync(content, ct);
            await handle.CommitAsync(ct);
        }

        CachedContent? hit = await store.TryGetAsync(key, ct);
        Assert.NotNull(hit);
        Assert.Equal(expectedETag, hit.ETag);
        await hit.Stream.DisposeAsync();
    }

    [Fact(Timeout = 120_000)]
    [Trait("Category", "Integration")]
    public async Task TryGetAsync_ReturnsNull_WhenKeyNotFound()
    {
        if (!fixture.DockerAvailable)
        {
            Assert.Skip("Docker is not available");
        }

        S3PackageContentStore store = fixture.CreateStore();

        CachedContent? result = await store.TryGetAsync("nonexistent/1.0.0/nonexistent.nupkg", TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact(Timeout = 120_000)]
    [Trait("Category", "Integration")]
    public async Task AbortedWrite_DoesNotPersistObject()
    {
        if (!fixture.DockerAvailable)
        {
            Assert.Skip("Docker is not available");
        }

        CancellationToken ct = TestContext.Current.CancellationToken;
        S3PackageContentStore store = fixture.CreateStore();
        string key = "test.package/2.0.0/test.package.2.0.0.nupkg";

        ICacheWriteHandle handle = await store.BeginWriteAsync(key, ct);
        handle.SetMetadata("application/octet-stream", 10, null);
        await handle.Stream.WriteAsync("incomplete"u8.ToArray(), ct);
        await handle.DisposeAsync();

        CachedContent? hit = await store.TryGetAsync(key, ct);
        Assert.Null(hit);
    }

    [Fact(Timeout = 120_000)]
    [Trait("Category", "Integration")]
    public async Task ConsecutiveCommits_OverwriteExistingObject()
    {
        if (!fixture.DockerAvailable)
        {
            Assert.Skip("Docker is not available");
        }

        CancellationToken ct = TestContext.Current.CancellationToken;
        S3PackageContentStore store = fixture.CreateStore();
        string key = "test.package/1.0.0/overwrite-test.1.0.0.nupkg";
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

        byte[] buffer = new byte[content2.Length];
        await hit.Stream.ReadExactlyAsync(buffer, ct);
        Assert.Equal(content2, buffer);

        await hit.Stream.DisposeAsync();
    }

    [Fact(Timeout = 120_000)]
    [Trait("Category", "Integration")]
    public async Task WriteAndRead_WithKeyPrefix()
    {
        if (!fixture.DockerAvailable)
        {
            Assert.Skip("Docker is not available");
        }

        CancellationToken ct = TestContext.Current.CancellationToken;
        S3PackageContentStore store = fixture.CreateStore("nuget-cache/");
        string key = "test.package/1.0.0/test.package.1.0.0.nupkg";
        byte[] content = "prefix-test"u8.ToArray();

        await using (ICacheWriteHandle handle = await store.BeginWriteAsync(key, ct))
        {
            handle.SetMetadata("application/octet-stream", content.Length, null);
            await handle.Stream.WriteAsync(content, ct);
            await handle.CommitAsync(ct);
        }

        CachedContent? hit = await store.TryGetAsync(key, ct);
        Assert.NotNull(hit);
        Assert.Equal(content.Length, hit.Length);

        byte[] buffer = new byte[content.Length];
        await hit.Stream.ReadExactlyAsync(buffer, ct);
        Assert.Equal(content, buffer);

        await hit.Stream.DisposeAsync();
    }

    [Fact(Timeout = 120_000)]
    [Trait("Category", "Integration")]
    public async Task RefreshTimestampAsync_UpdatesStoredAtUtc_PreservesContent()
    {
        if (!fixture.DockerAvailable)
        {
            Assert.Skip("Docker is not available");
        }

        CancellationToken ct = TestContext.Current.CancellationToken;
        S3PackageContentStore store = fixture.CreateStore();
        string key = "test.package/1.0.0/revalidate.1.0.0.nupkg";
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

        await Task.Delay(50, ct);

        DateTimeOffset newTimestamp = DateTimeOffset.UtcNow;
        await store.RefreshTimestampAsync(key, "application/octet-stream", "\"etag-2\"", newTimestamp, ct);

        CachedContent? secondRead = await store.TryGetAsync(key, ct);
        Assert.NotNull(secondRead);
        Assert.NotNull(secondRead.StoredAtUtc);
        Assert.True(secondRead.StoredAtUtc.Value > firstTimestamp, "StoredAtUtc should be updated.");
        Assert.Equal(newTimestamp.ToUnixTimeMilliseconds(), secondRead.StoredAtUtc.Value.ToUnixTimeMilliseconds());
        Assert.Equal("\"etag-2\"", secondRead.ETag);

        byte[] buffer = new byte[content.Length];
        int read = await secondRead.Stream.ReadAsync(buffer, ct);
        Assert.Equal(content.Length, read);
        Assert.Equal(content, buffer);

        await secondRead.Stream.DisposeAsync();
    }

    [Fact(Timeout = 120_000)]
    [Trait("Category", "Integration")]
    public async Task RefreshTimestampAsync_NoExistingContent_ThrowsS3Exception()
    {
        if (!fixture.DockerAvailable)
        {
            Assert.Skip("Docker is not available");
        }

        S3PackageContentStore store = fixture.CreateStore();
        CancellationToken ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<S3Exception>(
            () => store.RefreshTimestampAsync("nonexistent/1.0.0/file.nupkg", "application/octet-stream", null, DateTimeOffset.UtcNow, ct).AsTask());
    }
}
