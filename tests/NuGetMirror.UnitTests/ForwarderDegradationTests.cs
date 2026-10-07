using System.Net;
using System.Net.Http.Headers;
using System.Reflection;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NuGetMirror.Configuration;
using NuGetMirror.Diagnostics;
using NuGetMirror.Discovery;
using NuGetMirror.Proxy;
using NuGetMirror.Storage;
using NuGetMirror.TestKit;
using NuGetMirror.Upstream;

namespace NuGetMirror.UnitTests;

public sealed class ForwarderDegradationTests
{
    private const string RoutePrefix = "/v3-flatcontainer/";
    private const string UpstreamBase = "http://upstream.example/v3-flatcontainer/";
    private const string RemainingPath = "test.pkg/1.0.0/test.pkg.1.0.0.nupkg";
    private static readonly byte[] s_bodyBytes = "fake-nupkg-content"u8.ToArray();

    [Fact]
    public async Task StreamProxyCachedGet_Returns200_WhenBeginWriteThrowsUnauthorizedAccessException()
    {
        var bodyStream = new MemoryStream();
        DefaultHttpContext context = CreateHttpContext(bodyStream);
        var store = new StoreThatThrowsOnBeginWrite();
        Forwarder forwarder = CreateForwarder(store);

        await CallStreamProxyAsync(forwarder, context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("application/octet-stream", context.Response.ContentType);

        bodyStream.Position = 0;
        byte[] actual = bodyStream.ToArray();

        Assert.Equal(s_bodyBytes, actual);
    }

    [Fact]
    public async Task StreamProxyCachedGet_Returns200_WhenBeginWriteThrowsIOException()
    {
        var bodyStream = new MemoryStream();
        DefaultHttpContext context = CreateHttpContext(bodyStream);
        var store = new StoreThatThrowsOnBeginWrite(throwUnauthorizedAccess: false);
        Forwarder forwarder = CreateForwarder(store);

        await CallStreamProxyAsync(forwarder, context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("application/octet-stream", context.Response.ContentType);

        bodyStream.Position = 0;
        byte[] actual = bodyStream.ToArray();

        Assert.Equal(s_bodyBytes, actual);
    }

    [Fact]
    public async Task StreamProxyCachedGet_Returns200_WhenCacheWriteFailsMidStream()
    {
        var bodyStream = new MemoryStream();
        DefaultHttpContext context = CreateHttpContext(bodyStream);
        var handle = new FailingCacheWriteHandle();
        var store = new StoreWithCustomHandle(handle);
        Forwarder forwarder = CreateForwarder(store);

        await CallStreamProxyAsync(forwarder, context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.False(handle.Committed, "Handle should not be committed when cache write fails.");

        bodyStream.Position = 0;
        byte[] actual = bodyStream.ToArray();

        Assert.Equal(s_bodyBytes, actual);
    }

    private static DefaultHttpContext CreateHttpContext(MemoryStream responseBody)
    {
        var context = new DefaultHttpContext();

        context.Request.Method = HttpMethods.Get;
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("localhost:5049");

        context.Response.Body = responseBody;

        return context;
    }

    private static async Task CallStreamProxyAsync(Forwarder forwarder, DefaultHttpContext context)
    {
        await forwarder.StreamProxyAsync(context, RoutePrefix, RemainingPath);
    }

    private static Forwarder CreateForwarder(IPackageContentStore store)
    {
        IOptions<MirrorOptions> optionsWrapper = Microsoft.Extensions.Options.Options.Create(new MirrorOptions
        {
            Cache = new CacheOptions
            {
                Enabled = true,
                Backend = "FileSystem",
                FileSystem = new FileSystemOptions { Directory = "/tmp" },
            },
        });

        var handler = new StubUpstreamHandler();
        string upstreamUrl = UpstreamBase + RemainingPath;

        handler.MapFactory(upstreamUrl, () =>
        {
            var content = new ByteArrayContent(s_bodyBytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
            };

            return response;
        });

        var httpClient = new TestHttpClientFactory(handler);
        var upstreamClient = new UpstreamClient(httpClient, optionsWrapper, NullLogger<UpstreamClient>.Instance, new MirrorMetrics());

        var cache = new DiscoveryCache(upstreamClient, optionsWrapper, NullLogger<DiscoveryCache>.Instance, new MirrorMetrics(), TimeProvider.System);
        SeedSnapshot(cache);

        var keyedLock = new KeyedAsyncLock(NullLogger<KeyedAsyncLock>.Instance);

        return new Forwarder(upstreamClient, cache, optionsWrapper, NullLogger<Forwarder>.Instance, new MirrorMetrics(), store, keyedLock);
    }

    private static void SeedSnapshot(DiscoveryCache cache)
    {
        var snapshot = new DiscoverySnapshot(
            DateTimeOffset.UtcNow,
            "{}",
            new Dictionary<string, string> { { RoutePrefix, UpstreamBase } },
            []);

        FieldInfo field = typeof(DiscoveryCache).GetField(
            "_current",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("DiscoveryCache._current field not found.");

        field.SetValue(cache, snapshot);
    }

    private sealed class StoreThatThrowsOnBeginWrite(bool throwUnauthorizedAccess = true) : IPackageContentStore
    {
        public ValueTask<CachedContent?> TryGetAsync(string key, CancellationToken ct)
            => ValueTask.FromResult<CachedContent?>(null);

        public ValueTask<ICacheWriteHandle> BeginWriteAsync(string key, CancellationToken ct)
        {
            if (throwUnauthorizedAccess)
            {
                throw new UnauthorizedAccessException("Simulated permission denied");
            }

            throw new IOException("Simulated disk error");
        }
    }

    private sealed class StoreWithCustomHandle(ICacheWriteHandle handle) : IPackageContentStore
    {
        public ValueTask<CachedContent?> TryGetAsync(string key, CancellationToken ct)
            => ValueTask.FromResult<CachedContent?>(null);

        public ValueTask<ICacheWriteHandle> BeginWriteAsync(string key, CancellationToken ct)
            => ValueTask.FromResult(handle);
    }

    private sealed class FailingCacheWriteHandle : ICacheWriteHandle
    {
        private readonly MemoryStream _innerStream = new();

        public Stream Stream { get; }

        public bool Committed { get; private set; }

        public FailingCacheWriteHandle()
            => Stream = new FailingWriteStream(_innerStream);

        public void SetMetadata(string contentType, long length, string? etag)
        {
        }

        public ValueTask CommitAsync(CancellationToken ct)
        {
            Committed = true;
            return ValueTask.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            await Stream.DisposeAsync();
            await _innerStream.DisposeAsync();
        }
    }

    private sealed class FailingWriteStream(MemoryStream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => true;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
            => ValueTask.FromException(new IOException("Simulated cache write failure"));
    }
}
