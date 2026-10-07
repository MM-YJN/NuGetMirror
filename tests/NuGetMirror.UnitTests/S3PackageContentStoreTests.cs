using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NuGetMirror.Configuration;
using NuGetMirror.Storage;
using NuGetMirror.Storage.S3;

namespace NuGetMirror.UnitTests;

public sealed class S3PackageContentStoreTests
{
    [Fact]
    public async Task TryGetAsync_ReturnsNull_WhenKeyNotFound()
    {
        IPackageContentStore store = CreateStore();

        CachedContent? result = await store.TryGetAsync("nonexistent/1.0.0/nonexistent.nupkg", TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public void BuildS3Key_NoPrefix_ReturnsKeyWithLeadingSlashTrimmed()
    {
        var store = (S3PackageContentStore)CreateStore();

        string key = BuildS3Key(store, "/test.package/1.0.0/test.package.nupkg");

        Assert.Equal("test.package/1.0.0/test.package.nupkg", key);
    }

    [Fact]
    public void BuildS3Key_WithPrefix_PrependsPrefix()
    {
        var store = (S3PackageContentStore)CreateStore("packages/");

        string key = BuildS3Key(store, "test.package/1.0.0/test.package.nupkg");

        Assert.Equal("packages/test.package/1.0.0/test.package.nupkg", key);
    }

    [Fact]
    public void BuildS3Key_PrefixWithTrailingSlash_DoesNotDoubleSlash()
    {
        var store = (S3PackageContentStore)CreateStore("packages////");

        string key = BuildS3Key(store, "test.package/1.0.0/test.package.nupkg");

        Assert.Equal("packages/test.package/1.0.0/test.package.nupkg", key);
    }

    [Fact]
    public async Task EnumerateAsync_WithPrefix_StripsPrefixFromKeys()
    {
        S3MaintenanceTestHandler handler = CreateMaintenanceHandler();
        handler.AddListResponse("""
        <ListBucketResult>
          <Name>test-bucket</Name>
          <Prefix>packages/</Prefix>
          <KeyCount>1</KeyCount>
          <MaxKeys>1000</MaxKeys>
          <IsTruncated>false</IsTruncated>
          <Contents>
            <Key>packages/pkg.a/1.0.0/pkg.a.nupkg</Key>
            <Size>12345</Size>
            <LastModified>2025-01-15T10:30:00.000Z</LastModified>
          </Contents>
        </ListBucketResult>
        """);

        IPackageContentStore store = CreateStoreFromHandler("packages/", handler);

        var entries = new List<CacheEntryInfo>();
        await foreach (CacheEntryInfo entry in ((ICacheMaintenance)store).EnumerateAsync(TestContext.Current.CancellationToken))
        {
            entries.Add(entry);
        }

        Assert.Single(entries);
        Assert.Equal("pkg.a/1.0.0/pkg.a.nupkg", entries[0].Key);
        Assert.Equal(12345, entries[0].Length);
    }

    [Fact]
    public async Task EnumerateAsync_WithoutPrefix_KeepsKeys()
    {
        S3MaintenanceTestHandler handler = CreateMaintenanceHandler();
        handler.AddListResponse("""
        <ListBucketResult>
          <Prefix></Prefix>
          <KeyCount>1</KeyCount>
          <MaxKeys>1000</MaxKeys>
          <IsTruncated>false</IsTruncated>
          <Contents>
            <Key>pkg.a/1.0.0/pkg.a.nupkg</Key>
            <Size>100</Size>
            <LastModified>2025-01-15T10:30:00.000Z</LastModified>
          </Contents>
        </ListBucketResult>
        """);

        IPackageContentStore store = CreateStoreFromHandler("", handler);

        var entries = new List<CacheEntryInfo>();
        await foreach (CacheEntryInfo entry in ((ICacheMaintenance)store).EnumerateAsync(TestContext.Current.CancellationToken))
        {
            entries.Add(entry);
        }

        Assert.Single(entries);
        Assert.Equal("pkg.a/1.0.0/pkg.a.nupkg", entries[0].Key);
    }

    [Fact]
    public async Task EnumerateAsync_SkipsDirectoryMarkers()
    {
        S3MaintenanceTestHandler handler = CreateMaintenanceHandler();
        handler.AddListResponse("""
        <ListBucketResult>
          <Prefix>packages/</Prefix>
          <KeyCount>2</KeyCount>
          <MaxKeys>1000</MaxKeys>
          <IsTruncated>false</IsTruncated>
          <Contents>
            <Key>packages/</Key>
            <Size>0</Size>
            <LastModified>2025-01-01T00:00:00.000Z</LastModified>
          </Contents>
          <Contents>
            <Key>packages/pkg.a/1.0.0/pkg.a.nupkg</Key>
            <Size>200</Size>
            <LastModified>2025-01-15T10:30:00.000Z</LastModified>
          </Contents>
        </ListBucketResult>
        """);

        IPackageContentStore store = CreateStoreFromHandler("packages/", handler);

        var entries = new List<CacheEntryInfo>();
        await foreach (CacheEntryInfo entry in ((ICacheMaintenance)store).EnumerateAsync(TestContext.Current.CancellationToken))
        {
            entries.Add(entry);
        }

        Assert.Single(entries);
        Assert.Equal("pkg.a/1.0.0/pkg.a.nupkg", entries[0].Key);
    }

    [Fact]
    public async Task DeleteAsync_CallsClientDeleteObject()
    {
        S3MaintenanceTestHandler handler = CreateMaintenanceHandler();
        handler.DeleteStatus = HttpStatusCode.NoContent;
        IPackageContentStore store = CreateStoreFromHandler("", handler);

        await ((ICacheMaintenance)store).DeleteAsync("pkg.a/1.0.0/pkg.a.nupkg", TestContext.Current.CancellationToken);

        Assert.True(handler.DeleteWasCalled);
        Assert.Equal("pkg.a/1.0.0/pkg.a.nupkg", handler.LastDeletedKey);
    }

    [Fact]
    public async Task TouchAsync_CallsClientCopyObject()
    {
        S3MaintenanceTestHandler handler = CreateMaintenanceHandler();
        handler.CopyStatus = HttpStatusCode.OK;
        IPackageContentStore store = CreateStoreFromHandler("", handler);

        await ((ICacheMaintenance)store).TouchAsync("pkg.a/1.0.0/pkg.a.nupkg", TestContext.Current.CancellationToken);

        Assert.True(handler.CopyWasCalled);
        Assert.Equal("pkg.a/1.0.0/pkg.a.nupkg", handler.LastCopiedKey);
    }

    [Fact]
    public async Task RefreshTimestampAsync_CallsClientRevalidateObject()
    {
        S3MaintenanceTestHandler handler = CreateMaintenanceHandler();
        handler.RevalidateStatus = HttpStatusCode.OK;
        CancellationToken ct = TestContext.Current.CancellationToken;
        IPackageContentStore store = CreateStoreFromHandler("", handler);
        var revalidation = (ICacheRevalidation)store;

        var now = new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);
        await revalidation.RefreshTimestampAsync("pkg.a/1.0.0/pkg.a.nupkg", "application/octet-stream", "\"etag-x\"", now, ct);

        Assert.True(handler.RevalidateWasCalled);
        Assert.Equal("pkg.a/1.0.0/pkg.a.nupkg", handler.LastRevalidatedKey);
        Assert.Equal("\"etag-x\"", handler.LastRevalidatedEtag);
        Assert.Equal(now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture), handler.LastRevalidatedFetchedAt);
        Assert.Equal("application/octet-stream", handler.LastRevalidatedContentType);
    }

    [Fact]
    public async Task RefreshTimestampAsync_WithoutEtag_OmitsEtagMetaHeader()
    {
        S3MaintenanceTestHandler handler = CreateMaintenanceHandler();
        handler.RevalidateStatus = HttpStatusCode.OK;
        CancellationToken ct = TestContext.Current.CancellationToken;
        IPackageContentStore store = CreateStoreFromHandler("", handler);
        var revalidation = (ICacheRevalidation)store;

        await revalidation.RefreshTimestampAsync("pkg.a/1.0.0/pkg.a.nupkg", "application/octet-stream", null, DateTimeOffset.UtcNow, ct);

        Assert.True(handler.RevalidateWasCalled);
        Assert.Null(handler.LastRevalidatedEtag);
    }

    private static IPackageContentStore CreateStore(string keyPrefix = "")
    {
        var mirrorOptions = new MirrorOptions
        {
            Cache = new CacheOptions
            {
                Enabled = true,
                S3 = new S3Options
                {
                    Bucket = "test-bucket",
                    Region = "us-east-1",
                    AccessKey = "test",
                    SecretKey = "test",
                    ServiceUrl = "http://localhost:9000",
                    UsePathStyle = true,
                    KeyPrefix = keyPrefix,
                },
            },
        };

        IOptions<MirrorOptions> optionsWrapper = Microsoft.Extensions.Options.Options.Create(mirrorOptions);

        using var handler = new FakeS3HttpHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:9000") };
        var s3Client = new S3Client(httpClient, optionsWrapper, NullLogger<S3Client>.Instance, TimeProvider.System);
        return new S3PackageContentStore(s3Client, optionsWrapper, NullLogger<S3PackageContentStore>.Instance);
    }

    private static IPackageContentStore CreateStoreFromHandler(string keyPrefix, S3MaintenanceTestHandler handler)
    {
        var mirrorOptions = new MirrorOptions
        {
            Cache = new CacheOptions
            {
                Enabled = true,
                S3 = new S3Options
                {
                    Bucket = "test-bucket",
                    Region = "us-east-1",
                    AccessKey = "test",
                    SecretKey = "test",
                    ServiceUrl = "http://localhost:9000",
                    UsePathStyle = true,
                    KeyPrefix = keyPrefix,
                },
            },
        };

        IOptions<MirrorOptions> optionsWrapper = Microsoft.Extensions.Options.Options.Create(mirrorOptions);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:9000") };
        var s3Client = new S3Client(httpClient, optionsWrapper, NullLogger<S3Client>.Instance, TimeProvider.System);
        return new S3PackageContentStore(s3Client, optionsWrapper, NullLogger<S3PackageContentStore>.Instance);
    }

    private static S3MaintenanceTestHandler CreateMaintenanceHandler() => new();

    private static string BuildS3Key(S3PackageContentStore store, string key)
    {
        MethodInfo method = typeof(S3PackageContentStore).GetMethod(
            "BuildS3Key",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?? throw new InvalidOperationException("S3PackageContentStore.BuildS3Key method not found.");

        return (string)(method.Invoke(store, [key])
            ?? throw new InvalidOperationException("Invoke returned null."));
    }

    private sealed class FakeS3HttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }

    private sealed class S3MaintenanceTestHandler : HttpMessageHandler
    {
        private readonly Queue<string> _listResponses = new();
        private bool _headIssued;

        public HttpStatusCode DeleteStatus { get; set; } = HttpStatusCode.NotFound;
        public HttpStatusCode CopyStatus { get; set; } = HttpStatusCode.OK;
        public HttpStatusCode RevalidateStatus { get; set; } = HttpStatusCode.OK;
        public HttpStatusCode HeadStatus { get; set; } = HttpStatusCode.OK;
        public string? HeadEtag { get; set; }
        public string? HeadFetchedAt { get; set; }
        public bool DeleteWasCalled { get; private set; }
        public bool CopyWasCalled { get; private set; }
        public bool RevalidateWasCalled { get; private set; }
        public bool HeadWasCalled { get; private set; }
        public string? LastDeletedKey { get; private set; }
        public string? LastCopiedKey { get; private set; }
        public string? LastRevalidatedKey { get; private set; }
        public string? LastRevalidatedEtag { get; private set; }
        public string? LastRevalidatedFetchedAt { get; private set; }
        public string? LastRevalidatedContentType { get; private set; }

        public void AddListResponse(string xml) => _listResponses.Enqueue(xml);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Head)
            {
                _headIssued = true;
                HeadWasCalled = true;
                var headMsg = new HttpResponseMessage(HeadStatus);

                if (HeadStatus == HttpStatusCode.OK)
                {
                    headMsg.Content = new StringContent(string.Empty);
                    headMsg.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

                    if (HeadEtag is not null)
                    {
                        headMsg.Headers.TryAddWithoutValidation("x-amz-meta-etag", HeadEtag);
                    }

                    if (HeadFetchedAt is not null)
                    {
                        headMsg.Headers.TryAddWithoutValidation("x-amz-meta-fetched-at", HeadFetchedAt);
                    }
                }

                return Task.FromResult(headMsg);
            }

            if (request.Method == HttpMethod.Delete)
            {
                DeleteWasCalled = true;
                ArgumentNullException.ThrowIfNull(request.RequestUri);
                LastDeletedKey = ExtractKeyFromUri(request.RequestUri);
                return Task.FromResult(new HttpResponseMessage(DeleteStatus));
            }

            if (request.Method == HttpMethod.Put && request.Headers.Contains("x-amz-copy-source"))
            {
                bool isCopy = _headIssued;
                _headIssued = false;

                if (isCopy)
                {
                    CopyWasCalled = true;
                    ArgumentNullException.ThrowIfNull(request.RequestUri);
                    LastCopiedKey = ExtractKeyFromUri(request.RequestUri);
                    return Task.FromResult(new HttpResponseMessage(CopyStatus));
                }

                RevalidateWasCalled = true;
                ArgumentNullException.ThrowIfNull(request.RequestUri);
                LastRevalidatedKey = ExtractKeyFromUri(request.RequestUri);
                LastRevalidatedEtag = request.Headers.TryGetValues("x-amz-meta-etag", out IEnumerable<string>? etagValues) ? etagValues.FirstOrDefault() : null;
                LastRevalidatedFetchedAt = request.Headers.TryGetValues("x-amz-meta-fetched-at", out IEnumerable<string>? fetchedAtValues) ? fetchedAtValues.FirstOrDefault() : null;
                LastRevalidatedContentType = request.Content?.Headers.ContentType?.ToString();
                return Task.FromResult(new HttpResponseMessage(RevalidateStatus));
            }

            if (_listResponses.Count > 0)
            {
                string xml = _listResponses.Dequeue();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(xml, Encoding.UTF8, "application/xml"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static string ExtractKeyFromUri(Uri uri)
        {
            string path = uri.AbsolutePath;
            string bucket = "/test-bucket/";
            if (path.StartsWith(bucket, StringComparison.Ordinal))
            {
                path = path[bucket.Length..];
            }

            return Uri.UnescapeDataString(path);
        }
    }
}
