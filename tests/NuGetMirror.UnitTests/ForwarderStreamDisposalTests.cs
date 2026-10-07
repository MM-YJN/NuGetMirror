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

public sealed class ForwarderStreamDisposalTests
{
    private const string RoutePrefix = "/v3-flatcontainer/";
    private const string UpstreamBase = "http://upstream.example/v3-flatcontainer/";
    private const string RemainingPath = "test.pkg/1.0.0/test.pkg.1.0.0.nupkg";
    private static readonly byte[] s_bodyBytes = "fake-nupkg-content"u8.ToArray();

    [Fact]
    public async Task CacheHit_DisposesStream_WhenCopyToResponseSucceeds()
    {
        var trackingStream = new DisposeTrackingStream(new MemoryStream(s_bodyBytes));
        var responseBody = new MemoryStream();
        DefaultHttpContext context = CreateHttpContext(responseBody);
        var store = new StoreWithTrackingStream(trackingStream);
        Forwarder forwarder = CreateForwarder(store);

        await forwarder.StreamProxyAsync(context, RoutePrefix, RemainingPath);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.True(trackingStream.DisposeAsyncCalled || trackingStream.DisposeCalled,
            "Cached stream should be disposed after successful copy to response.");
    }

    [Fact]
    public async Task CacheHit_DisposesStream_WhenCopyToResponseThrows()
    {
        var trackingStream = new DisposeTrackingStream(new MemoryStream(s_bodyBytes));
        var throwingBody = new ThrowAfterBytesStream(maxBytes: 5);
        DefaultHttpContext context = CreateHttpContext(throwingBody);
        var store = new StoreWithTrackingStream(trackingStream);
        Forwarder forwarder = CreateForwarder(store);

        try
        {
            await forwarder.StreamProxyAsync(context, RoutePrefix, RemainingPath);
        }
        catch (IOException)
        {
        }

        Assert.True(trackingStream.DisposeAsyncCalled || trackingStream.DisposeCalled,
            "Cached stream should be disposed even when CopyToAsync fails.");
    }

    [Fact]
    public async Task StreamResponseWithoutCachingAsync_StreamsBody_ForNon200Status()
    {
        // The normalized StreamResponseWithoutCachingAsync should copy the response body
        // regardless of status code (old Forwarder variant only copied on 200).
        var responseBody = new MemoryStream();
        DefaultHttpContext context = CreateHttpContext(responseBody);
        byte[] bodyBytes = "not-found-body"u8.ToArray();

        using var responseMessage = new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new ByteArrayContent(bodyBytes),
        };
        responseMessage.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        responseMessage.Content.Headers.ContentLength = bodyBytes.Length;

        await ProxyHttp.StreamResponseWithoutCachingAsync(context, responseMessage, CancellationToken.None);

        Assert.Equal((int)HttpStatusCode.NotFound, context.Response.StatusCode);
        Assert.Equal("text/plain", context.Response.ContentType);
        Assert.True(responseBody.Length > 0, "Response body should be streamed even for non-200 status.");
    }

    private static DefaultHttpContext CreateHttpContext(Stream responseBody)
    {
        var context = new DefaultHttpContext();

        context.Request.Method = HttpMethods.Get;
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("localhost:5049");
        context.Response.Body = responseBody;

        return context;
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
        // DiscoveryCache has no public seeding API; reflection sets the backing field
        // so CreateForwarder can complete construction without discovery failing.
        // These tests exercise cache-hit paths which never reach discovery, so the
        // forward-map key mismatch ("PackageBaseAddress/3.0.0" vs "/v3-flatcontainer/")
        // is harmless.
        var snapshot = new DiscoverySnapshot(
            DateTimeOffset.UtcNow,
            "{}",
            new Dictionary<string, string>
            {
                ["PackageBaseAddress/3.0.0"] = UpstreamBase,
            },
            []);
        FieldInfo field = typeof(DiscoveryCache).GetField(
            "_current",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("DiscoveryCache._current field not found.");

        field.SetValue(cache, snapshot);
    }

    private sealed class StoreWithTrackingStream(DisposeTrackingStream trackingStream) : IPackageContentStore, ICacheMaintenance
    {
        public ValueTask<CachedContent?> TryGetAsync(string key, CancellationToken ct)
            => ValueTask.FromResult<CachedContent?>(new CachedContent
            {
                Stream = trackingStream,
                Length = s_bodyBytes.Length,
                ContentType = "application/octet-stream",
            });

        public ValueTask<ICacheWriteHandle> BeginWriteAsync(string key, CancellationToken ct)
            => throw new NotSupportedException();

        public async IAsyncEnumerable<CacheEntryInfo> EnumerateAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DeleteAsync(string key, CancellationToken ct)
            => ValueTask.CompletedTask;

        public ValueTask TouchAsync(string key, CancellationToken ct)
            => ValueTask.CompletedTask;
    }

    private sealed class DisposeTrackingStream(Stream inner) : Stream
    {
        private bool _disposed;

        public bool DisposeCalled => _disposed;

        public bool DisposeAsyncCalled { get; private set; }

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
            => inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            => inner.ReadAsync(buffer, ct);

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override async ValueTask DisposeAsync()
        {
            DisposeAsyncCalled = true;
            await inner.DisposeAsync();
            await base.DisposeAsync();
        }

        protected override void Dispose(bool disposing)
        {
            _disposed = true;

            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class ThrowAfterBytesStream(long maxBytes) : Stream
    {
        private long _written;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            _written += count;

            if (_written > maxBytes)
            {
                throw new IOException("Simulated client disconnect.");
            }
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            _written += buffer.Length;

            if (_written > maxBytes)
            {
                throw new IOException("Simulated client disconnect.");
            }

            await Task.CompletedTask;
        }
    }
}
