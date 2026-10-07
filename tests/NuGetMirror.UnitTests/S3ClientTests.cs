using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NuGetMirror.Configuration;
using NuGetMirror.Storage.S3;

namespace NuGetMirror.UnitTests;

public sealed class S3ClientTests
{
    [Fact]
    public async Task ListObjectsAsync_ReturnsEntries_ForSinglePage()
    {
        var handler = new S3TestHandler();
        handler.AddListResponse("""
        <ListBucketResult>
          <Name>test-bucket</Name>
          <Prefix></Prefix>
          <KeyCount>2</KeyCount>
          <MaxKeys>1000</MaxKeys>
          <IsTruncated>false</IsTruncated>
          <Contents>
            <Key>packages/pkg.a/1.0.0/pkg.a.nupkg</Key>
            <Size>12345</Size>
            <LastModified>2025-01-15T10:30:00.000Z</LastModified>
            <ETag>"abc"</ETag>
            <StorageClass>STANDARD</StorageClass>
          </Contents>
          <Contents>
            <Key>packages/pkg.b/2.0.0/pkg.b.nupkg</Key>
            <Size>67890</Size>
            <LastModified>2025-02-20T14:00:00.000Z</LastModified>
            <ETag>"def"</ETag>
            <StorageClass>STANDARD</StorageClass>
          </Contents>
        </ListBucketResult>
        """);

        S3Client client = CreateClient(handler);
        IReadOnlyList<S3ListEntry> results = await client.ListObjectsAsync("", TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        Assert.Equal("packages/pkg.a/1.0.0/pkg.a.nupkg", results[0].Key);
        Assert.Equal(12345, results[0].Size);
        Assert.Equal(67890, results[1].Size);
    }

    [Fact]
    public async Task ListObjectsAsync_HandlesPagination()
    {
        var handler = new S3TestHandler();
        handler.AddListResponse("""
        <ListBucketResult>
          <Name>test-bucket</Name>
          <Prefix>packages/</Prefix>
          <KeyCount>1</KeyCount>
          <MaxKeys>1000</MaxKeys>
          <IsTruncated>true</IsTruncated>
          <NextContinuationToken>token-page2</NextContinuationToken>
          <Contents>
            <Key>packages/pkg.a/1.0.0/pkg.a.nupkg</Key>
            <Size>100</Size>
            <LastModified>2025-01-15T10:30:00.000Z</LastModified>
          </Contents>
        </ListBucketResult>
        """);
        handler.AddListResponse("""
        <ListBucketResult>
          <Name>test-bucket</Name>
          <Prefix>packages/</Prefix>
          <KeyCount>1</KeyCount>
          <MaxKeys>1000</MaxKeys>
          <IsTruncated>false</IsTruncated>
          <Contents>
            <Key>packages/pkg.b/2.0.0/pkg.b.nupkg</Key>
            <Size>200</Size>
            <LastModified>2025-02-20T14:00:00.000Z</LastModified>
          </Contents>
        </ListBucketResult>
        """);

        S3Client client = CreateClient(handler);
        IReadOnlyList<S3ListEntry> results = await client.ListObjectsAsync("packages/", TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        Assert.Equal(200, results[1].Size);
    }

    [Fact]
    public async Task ListObjectsAsync_EmptyResponse_ReturnsEmpty()
    {
        var handler = new S3TestHandler();
        handler.AddListResponse("""
        <ListBucketResult>
          <Name>test-bucket</Name>
          <Prefix>nonexistent/</Prefix>
          <KeyCount>0</KeyCount>
          <MaxKeys>1000</MaxKeys>
          <IsTruncated>false</IsTruncated>
        </ListBucketResult>
        """);

        S3Client client = CreateClient(handler);
        IReadOnlyList<S3ListEntry> results = await client.ListObjectsAsync("nonexistent/", TestContext.Current.CancellationToken);

        Assert.Empty(results);
    }

    [Fact]
    public async Task DeleteObjectAsync_NoContent_ReturnsSuccessfully()
    {
        var handler = new S3TestHandler { DeleteStatus = HttpStatusCode.NoContent };

        S3Client client = CreateClient(handler);
        await client.DeleteObjectAsync("packages/pkg.a/1.0.0/pkg.a.nupkg", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task DeleteObjectAsync_NotFound_ReturnsSuccessfully()
    {
        var handler = new S3TestHandler { DeleteStatus = HttpStatusCode.NotFound };

        S3Client client = CreateClient(handler);
        await client.DeleteObjectAsync("packages/nonexistent.nupkg", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task DeleteObjectAsync_Error_ThrowsS3Exception()
    {
        var handler = new S3TestHandler
        {
            DeleteStatus = HttpStatusCode.Forbidden,
            DeleteBody = "AccessDenied",
        };

        S3Client client = CreateClient(handler);
        S3Exception ex = await Assert.ThrowsAsync<S3Exception>(
            () => client.DeleteObjectAsync("packages/forbidden.nupkg", TestContext.Current.CancellationToken));

        Assert.Contains("AccessDenied", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PutObjectAsync_Ok_ReturnsSuccessfully()
    {
        var handler = new S3TestHandler { PutObjectStatus = HttpStatusCode.OK };

        S3Client client = CreateClient(handler);
        await client.PutObjectAsync("packages/pkg.a/1.0.0/pkg.a.nupkg", Stream.Null, "application/octet-stream", null, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task PutObjectAsync_Created_ReturnsSuccessfully()
    {
        var handler = new S3TestHandler { PutObjectStatus = HttpStatusCode.Created };

        S3Client client = CreateClient(handler);
        await client.PutObjectAsync("packages/pkg.a/1.0.0/pkg.a.nupkg", Stream.Null, "application/octet-stream", null, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task PutObjectAsync_Error_ThrowsS3Exception()
    {
        var handler = new S3TestHandler
        {
            PutObjectStatus = HttpStatusCode.Forbidden,
            PutObjectBody = "AccessDenied",
        };

        S3Client client = CreateClient(handler);
        S3Exception ex = await Assert.ThrowsAsync<S3Exception>(
            () => client.PutObjectAsync("packages/forbidden.nupkg", Stream.Null, "application/octet-stream", null, TestContext.Current.CancellationToken));

        Assert.Contains("AccessDenied", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PutObjectAsync_WithEtag_SetsEtagMetaHeader()
    {
        var handler = new S3TestHandler();
        S3Client client = CreateClient(handler);
        await client.PutObjectAsync("packages/pkg.a/1.0.0/pkg.a.nupkg", Stream.Null, "application/octet-stream", "\"abc123\"", TestContext.Current.CancellationToken);

        Assert.Equal("\"abc123\"", handler.LastPutObjectEtag);
    }

    [Fact]
    public async Task PutObjectAsync_WithoutEtag_OmitsEtagMetaHeader()
    {
        var handler = new S3TestHandler();
        S3Client client = CreateClient(handler);
        await client.PutObjectAsync("packages/pkg.a/1.0.0/pkg.a.nupkg", Stream.Null, "application/octet-stream", null, TestContext.Current.CancellationToken);

        Assert.Null(handler.LastPutObjectEtag);
    }

    [Fact]
    public async Task PutObjectAsync_SetsFetchedAtMetaHeader()
    {
        var handler = new S3TestHandler();
        S3Client client = CreateClient(handler);
        await client.PutObjectAsync("packages/pkg.a/1.0.0/pkg.a.nupkg", Stream.Null, "application/octet-stream", null, TestContext.Current.CancellationToken);

        Assert.NotNull(handler.LastPutObjectFetchedAt);
    }

    [Fact]
    public async Task PutObjectAsync_SetsContentType()
    {
        var handler = new S3TestHandler();
        S3Client client = CreateClient(handler);
        await client.PutObjectAsync("packages/pkg.a/1.0.0/pkg.a.nupkg", Stream.Null, "application/xml", null, TestContext.Current.CancellationToken);

        Assert.Equal("application/xml", handler.LastPutObjectContentType);
    }

    [Fact]
    public async Task PutObjectAsync_NullKey_ThrowsArgumentNullException()
    {
        S3Client client = CreateClient(new S3TestHandler());
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => client.PutObjectAsync(null!, Stream.Null, "application/octet-stream", null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CopyObjectAsync_Ok_ReturnsSuccessfully()
    {
        var handler = new S3TestHandler { CopyStatus = HttpStatusCode.OK };

        S3Client client = CreateClient(handler);
        await client.CopyObjectAsync("packages/pkg.a/1.0.0/pkg.a.nupkg", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CopyObjectAsync_NoContent_ReturnsSuccessfully()
    {
        var handler = new S3TestHandler { CopyStatus = HttpStatusCode.NoContent };

        S3Client client = CreateClient(handler);
        await client.CopyObjectAsync("packages/pkg.a/1.0.0/pkg.a.nupkg", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CopyObjectAsync_SetsCopySourceHeader()
    {
        string? copySource = null;
        var handler = new S3TestHandler
        {
            CopyStatus = HttpStatusCode.OK,
            OnRequest = req => copySource = req.Headers.TryGetValues("x-amz-copy-source", out IEnumerable<string>? v) ? v.FirstOrDefault() : null,
        };

        S3Client client = CreateClient(handler);
        await client.CopyObjectAsync("packages/pkg.a/1.0.0/pkg.a.nupkg", TestContext.Current.CancellationToken);

        Assert.Equal("/test-bucket/packages/pkg.a/1.0.0/pkg.a.nupkg", copySource);
    }

    [Fact]
    public async Task CopyObjectAsync_HeadsObjectFirst()
    {
        var handler = new S3TestHandler { CopyStatus = HttpStatusCode.OK };

        S3Client client = CreateClient(handler);
        await client.CopyObjectAsync("packages/pkg.a/1.0.0/pkg.a.nupkg", TestContext.Current.CancellationToken);

        Assert.True(handler.HeadWasCalled, "CopyObjectAsync must HEAD the object to preserve metadata through REPLACE.");
        Assert.True(handler.CopyWasCalled);
    }

    [Fact]
    public async Task CopyObjectAsync_PreservesEtagAndFetchedAtMetadata()
    {
        var fetchedAt = new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);
        var handler = new S3TestHandler
        {
            CopyStatus = HttpStatusCode.OK,
            HeadEtag = "\"abc123\"",
            HeadFetchedAt = fetchedAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
        };

        S3Client client = CreateClient(handler);
        await client.CopyObjectAsync("packages/pkg.a/1.0.0/pkg.a.nupkg", TestContext.Current.CancellationToken);

        Assert.Equal("\"abc123\"", handler.LastCopyEtag);
        Assert.Equal(fetchedAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture), handler.LastCopyFetchedAt);
        Assert.Equal("REPLACE", handler.LastCopyMetadataDirective);
    }

    [Fact]
    public async Task CopyObjectAsync_PreservesContentTypeFromHead()
    {
        var handler = new S3TestHandler
        {
            CopyStatus = HttpStatusCode.OK,
            HeadContentType = "application/xml",
        };

        S3Client client = CreateClient(handler);
        await client.CopyObjectAsync("packages/pkg.a/1.0.0/pkg.a.nuspec", TestContext.Current.CancellationToken);

        Assert.Equal("application/xml", handler.LastCopyContentType);
    }

    [Fact]
    public async Task CopyObjectAsync_NoOp_WhenObjectMissing()
    {
        var handler = new S3TestHandler { HeadStatus = HttpStatusCode.NotFound };

        S3Client client = CreateClient(handler);
        await client.CopyObjectAsync("packages/gone.nupkg", TestContext.Current.CancellationToken);

        Assert.True(handler.HeadWasCalled);
        Assert.False(handler.CopyWasCalled, "A missing object must not be self-copied.");
    }

    [Fact]
    public async Task RevalidateObjectAsync_Ok_ReturnsSuccessfully()
    {
        var handler = new S3TestHandler { RevalidateStatus = HttpStatusCode.OK };

        S3Client client = CreateClient(handler);
        await client.RevalidateObjectAsync("packages/pkg.a/1.0.0/pkg.a.nupkg", "application/octet-stream", null, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RevalidateObjectAsync_NoContent_ReturnsSuccessfully()
    {
        var handler = new S3TestHandler { RevalidateStatus = HttpStatusCode.NoContent };

        S3Client client = CreateClient(handler);
        await client.RevalidateObjectAsync("packages/pkg.a/1.0.0/pkg.a.nupkg", "application/octet-stream", null, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RevalidateObjectAsync_SetsCopySourceHeader()
    {
        var handler = new S3TestHandler();
        S3Client client = CreateClient(handler);
        await client.RevalidateObjectAsync("packages/pkg.a/1.0.0/pkg.a.nupkg", "application/octet-stream", null, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        Assert.NotNull(handler.LastRevalidatedCopySource);
        Assert.Equal("/test-bucket/packages/pkg.a/1.0.0/pkg.a.nupkg", handler.LastRevalidatedCopySource);
    }

    [Fact]
    public async Task RevalidateObjectAsync_SetsMetadataDirectiveReplace()
    {
        var handler = new S3TestHandler();
        S3Client client = CreateClient(handler);
        await client.RevalidateObjectAsync("packages/pkg.a/1.0.0/pkg.a.nupkg", "application/octet-stream", null, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        Assert.NotNull(handler.LastRevalidatedMetadataDirective);
        Assert.Equal("REPLACE", handler.LastRevalidatedMetadataDirective);
    }

    [Fact]
    public async Task RevalidateObjectAsync_WithEtag_SetsEtagMetaHeader()
    {
        var handler = new S3TestHandler();
        S3Client client = CreateClient(handler);
        await client.RevalidateObjectAsync("packages/pkg.a/1.0.0/pkg.a.nupkg", "application/octet-stream", "\"abc123\"", DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        Assert.Equal("\"abc123\"", handler.LastRevalidatedEtag);
    }

    [Fact]
    public async Task RevalidateObjectAsync_WithoutEtag_OmitsEtagMetaHeader()
    {
        var handler = new S3TestHandler();
        S3Client client = CreateClient(handler);
        await client.RevalidateObjectAsync("packages/pkg.a/1.0.0/pkg.a.nupkg", "application/octet-stream", null, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        Assert.Null(handler.LastRevalidatedEtag);
    }

    [Fact]
    public async Task RevalidateObjectAsync_SetsFetchedAtMetaHeader()
    {
        var handler = new S3TestHandler();
        S3Client client = CreateClient(handler);
        var now = new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);
        await client.RevalidateObjectAsync("packages/pkg.a/1.0.0/pkg.a.nupkg", "application/octet-stream", null, now, TestContext.Current.CancellationToken);

        Assert.Equal(now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture), handler.LastRevalidatedFetchedAt);
    }

    [Fact]
    public async Task RevalidateObjectAsync_SetsContentType()
    {
        var handler = new S3TestHandler();
        S3Client client = CreateClient(handler);
        await client.RevalidateObjectAsync("packages/pkg.a/1.0.0/pkg.a.nupkg", "application/xml", null, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);

        Assert.Equal("application/xml", handler.LastRevalidatedContentType);
    }

    [Fact]
    public async Task RevalidateObjectAsync_Error_ThrowsS3Exception()
    {
        var handler = new S3TestHandler
        {
            RevalidateStatus = HttpStatusCode.Forbidden,
            RevalidateBody = "AccessDenied",
        };

        S3Client client = CreateClient(handler);
        S3Exception ex = await Assert.ThrowsAsync<S3Exception>(
            () => client.RevalidateObjectAsync("packages/forbidden.nupkg", "application/octet-stream", null, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
        Assert.Contains("AccessDenied", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RevalidateObjectAsync_NullKey_ThrowsArgumentNullException()
    {
        S3Client client = CreateClient(new S3TestHandler());
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => client.RevalidateObjectAsync(null!, "application/octet-stream", null, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateBucketAsync_Ok_ReturnsSuccessfully()
    {
        var handler = new S3TestHandler { CreateBucketStatus = HttpStatusCode.OK };

        S3Client client = CreateClient(handler);
        await client.CreateBucketAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CreateBucketAsync_Conflict_ReturnsSuccessfully()
    {
        var handler = new S3TestHandler { CreateBucketStatus = HttpStatusCode.Conflict };

        S3Client client = CreateClient(handler);
        await client.CreateBucketAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CreateBucketAsync_Error_ThrowsS3Exception()
    {
        var handler = new S3TestHandler
        {
            CreateBucketStatus = HttpStatusCode.Forbidden,
            CreateBucketBody = "AccessDenied",
        };

        S3Client client = CreateClient(handler);
        S3Exception ex = await Assert.ThrowsAsync<S3Exception>(
            () => client.CreateBucketAsync(TestContext.Current.CancellationToken));

        Assert.Contains("AccessDenied", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckAsync_Ok_ReturnsSuccessfully()
    {
        var handler = new S3TestHandler { ListStatus = HttpStatusCode.OK };

        S3Client client = CreateClient(handler);
        await client.CheckAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CheckAsync_Error_ThrowsS3ExceptionWithBody()
    {
        var handler = new S3TestHandler
        {
            ListStatus = HttpStatusCode.Forbidden,
            ListBody = "AccessDenied",
        };

        S3Client client = CreateClient(handler);
        S3Exception ex = await Assert.ThrowsAsync<S3Exception>(
            () => client.CheckAsync(TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
        Assert.Contains("AccessDenied", ex.Message, StringComparison.Ordinal);
    }

    private static S3Client CreateClient(S3TestHandler handler)
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
                },
            },
        };

        IOptions<MirrorOptions> optionsWrapper = Microsoft.Extensions.Options.Options.Create(mirrorOptions);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:9000") };
        return new S3Client(httpClient, optionsWrapper, NullLogger<S3Client>.Instance, TimeProvider.System);
    }

    private sealed class S3TestHandler : HttpMessageHandler
    {
        private readonly Queue<string> _listResponses = [];
        private bool _headIssued;

        public Action<HttpRequestMessage>? OnRequest { get; set; }

        public HttpStatusCode DeleteStatus { get; set; } = HttpStatusCode.NotFound;

        public string? DeleteBody { get; set; }

        public HttpStatusCode PutObjectStatus { get; set; } = HttpStatusCode.OK;

        public string? PutObjectBody { get; set; }

        public HttpStatusCode CopyStatus { get; set; } = HttpStatusCode.OK;

        public HttpStatusCode RevalidateStatus { get; set; } = HttpStatusCode.OK;

        public string? RevalidateBody { get; set; }

        public HttpStatusCode CreateBucketStatus { get; set; } = HttpStatusCode.OK;

        public string? CreateBucketBody { get; set; }

        public HttpStatusCode HeadStatus { get; set; } = HttpStatusCode.OK;

        public string? HeadContentType { get; set; } = "application/octet-stream";

        public string? HeadEtag { get; set; }

        public string? HeadFetchedAt { get; set; }

        public HttpStatusCode ListStatus { get; set; } = HttpStatusCode.OK;

        public string? ListBody { get; set; }

        public bool HeadWasCalled { get; private set; }
        public bool CopyWasCalled { get; private set; }

        public string? LastRevalidatedCopySource { get; private set; }
        public string? LastRevalidatedMetadataDirective { get; private set; }
        public string? LastRevalidatedEtag { get; private set; }
        public string? LastRevalidatedFetchedAt { get; private set; }
        public string? LastRevalidatedContentType { get; private set; }

        public string? LastCopySource { get; private set; }
        public string? LastCopyMetadataDirective { get; private set; }
        public string? LastCopyEtag { get; private set; }
        public string? LastCopyFetchedAt { get; private set; }
        public string? LastCopyContentType { get; private set; }

        public string? LastPutObjectEtag { get; private set; }
        public string? LastPutObjectFetchedAt { get; private set; }
        public string? LastPutObjectContentType { get; private set; }

        public void AddListResponse(string xml) => _listResponses.Enqueue(xml);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            OnRequest?.Invoke(request);

            if (request.Method == HttpMethod.Head)
            {
                return Task.FromResult(HandleHead());
            }

            if (request.Method == HttpMethod.Delete)
            {
                return Task.FromResult(BuildResponse(DeleteStatus, DeleteBody));
            }

            if (request.Method == HttpMethod.Put && request.Headers.Contains("x-amz-copy-source"))
            {
                return Task.FromResult(HandleSelfCopy(request));
            }

            if (request.Method == HttpMethod.Put)
            {
                return Task.FromResult(HandlePut(request));
            }

            return Task.FromResult(HandleGet());
        }

        private HttpResponseMessage HandleHead()
        {
            _headIssued = true;
            HeadWasCalled = true;
            var headMsg = new HttpResponseMessage(HeadStatus);

            if (HeadStatus == HttpStatusCode.OK)
            {
                headMsg.Content = new StringContent(string.Empty);
                headMsg.Content.Headers.ContentType = new MediaTypeHeaderValue(HeadContentType ?? "application/octet-stream");

                if (HeadEtag is not null)
                {
                    headMsg.Headers.TryAddWithoutValidation("x-amz-meta-etag", HeadEtag);
                }

                if (HeadFetchedAt is not null)
                {
                    headMsg.Headers.TryAddWithoutValidation("x-amz-meta-fetched-at", HeadFetchedAt);
                }
            }

            return headMsg;
        }

        private HttpResponseMessage HandleSelfCopy(HttpRequestMessage request)
        {
            bool isCopy = _headIssued;
            _headIssued = false;

            if (isCopy)
            {
                CopyWasCalled = true;
                LastCopySource = HeaderValue(request, "x-amz-copy-source");
                LastCopyMetadataDirective = HeaderValue(request, "x-amz-metadata-directive");
                LastCopyEtag = HeaderValue(request, "x-amz-meta-etag");
                LastCopyFetchedAt = HeaderValue(request, "x-amz-meta-fetched-at");
                LastCopyContentType = request.Content?.Headers.ContentType?.ToString();
                return BuildResponse(CopyStatus, body: null);
            }

            LastRevalidatedCopySource = HeaderValue(request, "x-amz-copy-source");
            LastRevalidatedMetadataDirective = HeaderValue(request, "x-amz-metadata-directive");
            LastRevalidatedEtag = HeaderValue(request, "x-amz-meta-etag");
            LastRevalidatedFetchedAt = HeaderValue(request, "x-amz-meta-fetched-at");
            LastRevalidatedContentType = request.Content?.Headers.ContentType?.ToString();
            return BuildResponse(RevalidateStatus, RevalidateBody);
        }

        private HttpResponseMessage HandlePut(HttpRequestMessage request)
        {
            // Direct upload (PutObjectAsync) has meta-fetched-at; bucket creation does not.
            if (request.Headers.Contains("x-amz-meta-fetched-at"))
            {
                LastPutObjectEtag = HeaderValue(request, "x-amz-meta-etag");
                LastPutObjectFetchedAt = HeaderValue(request, "x-amz-meta-fetched-at");
                LastPutObjectContentType = request.Content?.Headers.ContentType?.ToString();
                return BuildResponse(PutObjectStatus, PutObjectBody);
            }

            return BuildResponse(CreateBucketStatus, CreateBucketBody);
        }

        private HttpResponseMessage HandleGet()
        {
            if (_listResponses.Count > 0)
            {
                string xml = _listResponses.Dequeue();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(xml, Encoding.UTF8, "application/xml"),
                };
            }

            // No queued list response: serve the CheckAsync probe / GetObjectAsync lookup.
            return BuildResponse(ListStatus, ListBody);
        }

        private static string? HeaderValue(HttpRequestMessage request, string name)
            => request.Headers.TryGetValues(name, out IEnumerable<string>? values) ? values.FirstOrDefault() : null;

        private static HttpResponseMessage BuildResponse(HttpStatusCode status, string? body)
        {
            var msg = new HttpResponseMessage(status);
            if (body is not null)
            {
                msg.Content = new StringContent(body);
            }

            return msg;
        }
    }
}
