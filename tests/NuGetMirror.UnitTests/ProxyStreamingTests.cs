using System.Net;
using System.Net.Http.Headers;
using System.Text;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

using NuGetMirror.Diagnostics;
using NuGetMirror.Proxy;

namespace NuGetMirror.UnitTests;

public sealed class ProxyStreamingTests
{
    private const string CacheKey = "test/pkg/1.0.0/test.pkg.1.0.0.nupkg";

    // ── TeeStreamAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task TeeStream_NormalCopy_WritesAllBytesToBothOutputs()
    {
        byte[] data = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        using var source = new MemoryStream(data);
        using var clientOutput = new MemoryStream();
        using var cacheOutput = new MemoryStream();

        (long copied, bool cacheOk) = await ProxyStreaming.TeeStreamAsync(
            source, clientOutput, cacheOutput, CacheKey, NullLogger.Instance, TestContext.Current.CancellationToken);

        Assert.Equal(data.Length, copied);
        Assert.True(cacheOk);
        Assert.Equal(data, clientOutput.ToArray());
        Assert.Equal(data, cacheOutput.ToArray());
    }

    [Fact]
    public async Task TeeStream_EmptySource_ReturnsZeroAndCacheOk()
    {
        using var source = new MemoryStream();
        using var clientOutput = new MemoryStream();
        using var cacheOutput = new MemoryStream();

        (long copied, bool cacheOk) = await ProxyStreaming.TeeStreamAsync(
            source, clientOutput, cacheOutput, CacheKey, NullLogger.Instance, TestContext.Current.CancellationToken);

        Assert.Equal(0, copied);
        Assert.True(cacheOk);
        Assert.Empty(clientOutput.ToArray());
        Assert.Empty(cacheOutput.ToArray());
    }

    [Fact]
    public async Task TeeStream_LargerThanBuffer_StillCopiesAllBytes()
    {
        // StreamCopyBufferSize is 64 KiB; write 80 KiB to span multiple reads.
        byte[] data = new byte[80 * 1024];
        Random.Shared.NextBytes(data);
        using var source = new MemoryStream(data);
        using var clientOutput = new MemoryStream();
        using var cacheOutput = new MemoryStream();

        (long copied, bool cacheOk) = await ProxyStreaming.TeeStreamAsync(
            source, clientOutput, cacheOutput, CacheKey, NullLogger.Instance, TestContext.Current.CancellationToken);

        Assert.Equal(data.Length, copied);
        Assert.True(cacheOk);
        Assert.Equal(data, clientOutput.ToArray());
        Assert.Equal(data, cacheOutput.ToArray());
    }

    [Fact]
    public async Task TeeStream_CacheWriteFailsMidStream_ContinuesToClient_ReturnsCacheNotOk()
    {
        byte[] data = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        using var source = new MemoryStream(data);
        using var clientOutput = new MemoryStream();
        using var cacheOutput = new ThrowOnWriteStream(afterBytes: 4);

        (long copied, bool cacheOk) = await ProxyStreaming.TeeStreamAsync(
            source, clientOutput, cacheOutput, CacheKey, NullLogger.Instance, TestContext.Current.CancellationToken);

        Assert.Equal(data.Length, copied);
        Assert.False(cacheOk, "cacheOk must be false once the cache output throws IOException.");
        Assert.Equal(data, clientOutput.ToArray());
    }

    [Fact]
    public async Task TeeStream_CacheFlushFails_ReturnsCacheNotOk()
    {
        byte[] data = new byte[] { 1, 2, 3 };
        using var source = new MemoryStream(data);
        using var clientOutput = new MemoryStream();
        using var cacheOutput = new ThrowOnFlushStream();

        (long copied, bool cacheOk) = await ProxyStreaming.TeeStreamAsync(
            source, clientOutput, cacheOutput, CacheKey, NullLogger.Instance, TestContext.Current.CancellationToken);

        Assert.Equal(data.Length, copied);
        Assert.False(cacheOk, "cacheOk must be false when the final cache FlushAsync throws.");
        Assert.Equal(data, clientOutput.ToArray());
    }

    [Fact]
    public async Task TeeStream_ClientCancellation_ThrowsOperationCanceledException()
    {
        byte[] data = new byte[1024];
        using var source = new SlowStream(data, delayPerRead: Timeout.InfiniteTimeSpan);
        using var clientOutput = new MemoryStream();
        using var cacheOutput = new MemoryStream();
        var cts = new CancellationTokenSource();

        Func<Task> act = async () =>
        {
            cts.CancelAfter(TimeSpan.FromMilliseconds(20));
            await ProxyStreaming.TeeStreamAsync(source, clientOutput, cacheOutput, CacheKey, NullLogger.Instance, cts.Token);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(act);
    }

    [Fact]
    public async Task TeeStream_ClientWriteFails_PropagatesException()
    {
        // When the *client* output throws, the exception propagates (cache failure is swallowed,
        // client failure is fatal). This also exercises the ArrayPool finally-block buffer return.
        byte[] data = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        using var source = new MemoryStream(data);
        using var clientOutput = new ThrowOnWriteStream(afterBytes: 2);
        using var cacheOutput = new MemoryStream();

        Func<Task<(long Copied, bool CacheOk)>> act = async () => await ProxyStreaming.TeeStreamAsync(
            source, clientOutput, cacheOutput, CacheKey, NullLogger.Instance, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<IOException>(act);
    }

    // ── ReadResponseBodyWithLimitAsync ───────────────────────────────────────

    [Fact]
    public async Task ReadResponseBody_WithinLimit_ReturnsExactBytes()
    {
        byte[] body = "hello-world"u8.ToArray();
        HttpResponseMessage response = BuildResponse(body, contentType: "application/json");
        DefaultHttpContext context = MakeContext();

        byte[]? result = await ProxyStreaming.ReadResponseBodyWithLimitAsync(
            context, response, maxBytes: 1024, "too large", new MirrorMetrics(), TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(body, result);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task ReadResponseBody_EmptyBody_ReturnsZeroLengthArray()
    {
        HttpResponseMessage response = BuildResponse([], contentType: "application/json");
        DefaultHttpContext context = MakeContext();

        byte[]? result = await ProxyStreaming.ReadResponseBodyWithLimitAsync(
            context, response, maxBytes: 1024, "too large", new MirrorMetrics(), TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public async Task ReadResponseBody_ExceedsLimit_ReturnsNullAndWrites502()
    {
        byte[] body = new byte[100];
        HttpResponseMessage response = BuildResponse(body, contentType: "application/json");
        DefaultHttpContext context = MakeContext();

        byte[]? result = await ProxyStreaming.ReadResponseBodyWithLimitAsync(
            context, response, maxBytes: 10, "Upstream response body exceeds maximum allowed size.", new MirrorMetrics(), TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.Equal(StatusCodes.Status502BadGateway, context.Response.StatusCode);
        Assert.Equal("application/problem+json", context.Response.ContentType);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8, leaveOpen: true);
        string text = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Upstream response body exceeds maximum allowed size.", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadResponseBody_ExactlyAtLimit_ReturnsBytes()
    {
        byte[] body = new byte[10];
        HttpResponseMessage response = BuildResponse(body, contentType: "application/json");
        DefaultHttpContext context = MakeContext();

        byte[]? result = await ProxyStreaming.ReadResponseBodyWithLimitAsync(
            context, response, maxBytes: 10, "too large", new MirrorMetrics(), TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(10, result.Length);
    }

    [Fact]
    public async Task ReadResponseBody_PreSizesFromContentLength_WhenWithinLimit()
    {
        // No externally observable behavior change, but guards the Content-Length pre-sizing path
        // (estimatedCapacity = cl when 0 < cl <= maxBytes). A body larger than the small default
        // 4096 capacity would force MemoryStream to grow if the pre-sizing path were broken.
        byte[] body = new byte[8192];
        Random.Shared.NextBytes(body);
        HttpResponseMessage response = BuildResponse(body, contentType: "application/json");
        DefaultHttpContext context = MakeContext();

        byte[]? result = await ProxyStreaming.ReadResponseBodyWithLimitAsync(
            context, response, maxBytes: 16384, "too large", new MirrorMetrics(), TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(body, result);
    }

    [Fact]
    public async Task ReadResponseBody_ContentLengthExceedsLimit_ReturnsNullAndWrites502()
    {
        // Content-Length claims 100 bytes, limit is 10. The first read should already overflow.
        byte[] body = new byte[100];
        HttpResponseMessage response = BuildResponse(body, contentType: "application/json");
        DefaultHttpContext context = MakeContext();

        byte[]? result = await ProxyStreaming.ReadResponseBodyWithLimitAsync(
            context, response, maxBytes: 10, "too large", new MirrorMetrics(), TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.Equal(StatusCodes.Status502BadGateway, context.Response.StatusCode);
    }

    [Fact]
    public async Task ReadResponseBody_DisposesUpstreamStream()
    {
        var upstreamStream = new DisposeTrackingStream(new MemoryStream("data"u8.ToArray()));
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(upstreamStream),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        DefaultHttpContext context = MakeContext();

        await ProxyStreaming.ReadResponseBodyWithLimitAsync(
            context, response, maxBytes: 1024, "too large", new MirrorMetrics(), TestContext.Current.CancellationToken);

        Assert.True(upstreamStream.DisposeCalled, "Upstream stream should be disposed after reading the body.");
    }

    [Fact]
    public async Task ReadResponseBody_Cancellation_ThrowsOperationCanceledException()
    {
        var upstreamStream = new SlowStream(new byte[1024], delayPerRead: Timeout.InfiniteTimeSpan);
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(upstreamStream),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        DefaultHttpContext context = MakeContext();
        var cts = new CancellationTokenSource();

        Func<Task> act = async () =>
        {
            cts.CancelAfter(TimeSpan.FromMilliseconds(20));
            await ProxyStreaming.ReadResponseBodyWithLimitAsync(
                context, response, maxBytes: 1024, "too large", new MirrorMetrics(), cts.Token);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(act);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static DefaultHttpContext MakeContext()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.Request.Method = HttpMethods.Get;
        return context;
    }

    private static HttpResponseMessage BuildResponse(byte[] body, string contentType)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Headers.ContentLength = body.Length;
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content,
        };
    }

    private sealed class ThrowOnWriteStream(long afterBytes) : Stream
    {
        private long _written;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            _written += count;
            if (_written > afterBytes)
            {
                throw new IOException("Simulated cache write failure.");
            }
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            _written += buffer.Length;
            if (_written > afterBytes)
            {
                throw new IOException("Simulated cache write failure.");
            }

            await Task.CompletedTask;
        }
    }

    private sealed class ThrowOnFlushStream : MemoryStream
    {
        public override Task FlushAsync(CancellationToken cancellationToken)
            => throw new IOException("Simulated cache flush failure.");

        public override void Flush()
            => throw new IOException("Simulated cache flush failure.");
    }

    private sealed class SlowStream(byte[] data, TimeSpan delayPerRead) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_position >= data.Length)
            {
                return 0;
            }

            await Task.Delay(delayPerRead, ct);
            int toCopy = Math.Min(buffer.Length, data.Length - _position);
            data.AsSpan(_position, toCopy).CopyTo(buffer.Span);
            _position += toCopy;
            return toCopy;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class DisposeTrackingStream(Stream inner) : Stream
    {
        private bool _disposed;

        public bool DisposeCalled => _disposed;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            => inner.ReadAsync(buffer, ct);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

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
}
