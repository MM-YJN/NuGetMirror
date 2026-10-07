using System.Net;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NuGetMirror.Configuration;
using NuGetMirror.Storage;
using NuGetMirror.Storage.S3;

namespace NuGetMirror.UnitTests;

public sealed class S3CachePreflightServiceTests
{
    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static S3Client CreateClient(IOptions<MirrorOptions> options, Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var httpClient = new HttpClient(new PreflightTestHandler(handler));
        return new S3Client(httpClient, options, NullLogger<S3Client>.Instance, TimeProvider.System);
    }

    private static S3CachePreflightService CreateService(IOptions<MirrorOptions> options, S3Client client)
        => new(options, client, NullLogger<S3CachePreflightService>.Instance);

    private static IOptions<MirrorOptions> EnabledOptions(string bucket = "my-bucket")
        => Options.Create(new MirrorOptions
        {
            Cache = new CacheOptions
            {
                Enabled = true,
                Backend = "S3",
                S3 = new S3Options
                {
                    Bucket = bucket,
                    Region = "us-east-1",
                    AccessKey = "key",
                    SecretKey = "secret",
                    UsePathStyle = true,
                    ServiceUrl = "http://localhost:9000",
                },
            },
        });

    // -----------------------------------------------------------------------
    // Tests
    // -----------------------------------------------------------------------

    [Fact]
    public async Task StartingAsync_NoOp_WhenCacheDisabled()
    {
        IOptions<MirrorOptions> options = Options.Create(new MirrorOptions
        {
            Cache = new CacheOptions
            {
                Enabled = false,
                Backend = "S3",
                S3 = new S3Options { Bucket = "my-bucket" },
            },
        });

        bool called = false;
        S3Client client = CreateClient(options, _ =>
        {
            called = true;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        S3CachePreflightService service = CreateService(options, client);
        await service.StartingAsync(CancellationToken.None);

        Assert.False(called, "S3 client should not be called when caching is disabled.");
    }

    [Fact]
    public async Task StartingAsync_NoOp_WhenBackendIsFileSystem()
    {
        IOptions<MirrorOptions> options = Options.Create(new MirrorOptions
        {
            Cache = new CacheOptions
            {
                Enabled = true,
                Backend = "FileSystem",
                S3 = new S3Options { Bucket = "my-bucket" },
            },
        });

        bool called = false;
        S3Client client = CreateClient(options, _ =>
        {
            called = true;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        S3CachePreflightService service = CreateService(options, client);
        await service.StartingAsync(CancellationToken.None);

        Assert.False(called, "S3 client should not be called when the backend is FileSystem.");
    }

    [Fact]
    public async Task StartingAsync_Throws_WhenBucketIsEmpty()
    {
        IOptions<MirrorOptions> options = Options.Create(new MirrorOptions
        {
            Cache = new CacheOptions
            {
                Enabled = true,
                Backend = "S3",
                S3 = new S3Options { Bucket = "" },
            },
        });

        S3Client client = CreateClient(options, _ => throw new InvalidOperationException("should not be called"));
        S3CachePreflightService service = CreateService(options, client);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.StartingAsync(CancellationToken.None));

        Assert.Contains("Bucket", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartingAsync_Throws_WhenBucketUnreachable()
    {
        IOptions<MirrorOptions> options = EnabledOptions("unreachable-bucket");
        S3Client client = CreateClient(options, _ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("AccessDenied"),
        });
        S3CachePreflightService service = CreateService(options, client);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.StartingAsync(CancellationToken.None));

        Assert.Contains("unreachable-bucket", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("S3 bucket", ex.Message, StringComparison.OrdinalIgnoreCase);
        // Inner exception should carry the original S3 error.
        Assert.IsType<S3Exception>(ex.InnerException);
    }

    [Fact]
    public async Task StartingAsync_Succeeds_WhenBucketReachable()
    {
        IOptions<MirrorOptions> options = EnabledOptions("reachable-bucket");
        S3Client client = CreateClient(options, _ => new HttpResponseMessage(HttpStatusCode.OK));
        S3CachePreflightService service = CreateService(options, client);

        // Should complete without throwing.
        await service.StartingAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartingAsync_Throws_WhenHttpRequestFails()
    {
        IOptions<MirrorOptions> options = EnabledOptions("error-bucket");
        S3Client client = CreateClient(options, _ => throw new HttpRequestException("connection refused"));
        S3CachePreflightService service = CreateService(options, client);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.StartingAsync(CancellationToken.None));

        Assert.Contains("error-bucket", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.IsType<HttpRequestException>(ex.InnerException);
    }

    private sealed class PreflightTestHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(handler(request));
    }
}
