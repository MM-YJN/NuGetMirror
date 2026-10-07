using System.Net;
using System.Net.Http.Headers;
using System.Text;

using Microsoft.AspNetCore.Http;

using NuGetMirror.Proxy;
using NuGetMirror.TestKit;

namespace NuGetMirror.UnitTests;

public sealed class ProxyHttpTests
{
    // ── MethodTag ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("GET", "get")]
    [InlineData("HEAD", "head")]
    [InlineData("DELETE", "delete")]
    [InlineData("POST", "post")]
    [InlineData("PUT", "put")]
    public void MethodTag_ReturnsLowercaseMethodToken(string method, string expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;

        Assert.Equal(expected, ProxyHttp.MethodTag(context));
    }

    [Fact]
    public void MethodTag_Get_ReturnsInternedConstant_NotAllocation()
    {
        // GET and HEAD are the hot-path methods; the helper must return a compile-time
        // constant string ("get" / "head") rather than a ToLowerInvariant allocation.
        var ctx1 = new DefaultHttpContext();
        ctx1.Request.Method = "GET";
        var ctx2 = new DefaultHttpContext();
        ctx2.Request.Method = "GET";

        Assert.Same(ProxyHttp.MethodTag(ctx1), ProxyHttp.MethodTag(ctx2));
    }

    [Fact]
    public void MethodTag_Head_ReturnsInternedConstant_NotAllocation()
    {
        var ctx1 = new DefaultHttpContext();
        ctx1.Request.Method = "HEAD";
        var ctx2 = new DefaultHttpContext();
        ctx2.Request.Method = "HEAD";

        Assert.Same(ProxyHttp.MethodTag(ctx1), ProxyHttp.MethodTag(ctx2));
    }

    // ── WriteBodyUtf8Async ───────────────────────────────────────────────────

    [Fact]
    public async Task WriteBodyUtf8Async_AsciiBody_SetsContentLengthAndReturnsCount()
    {
        DefaultHttpContext context = MakeContext();
        const string Body = "Hello, NuGet!";
        int expected = Encoding.UTF8.GetByteCount(Body);

        long returned = await ProxyHttp.WriteBodyUtf8Async(context, Body, TestContext.Current.CancellationToken);

        Assert.Equal(expected, returned);
        Assert.Equal(expected, context.Response.ContentLength);
    }

    [Fact]
    public async Task WriteBodyUtf8Async_MultiByteBody_SetsCorrectContentLength()
    {
        // UTF-8 encodes these characters as 3 bytes each; a char-count ≠ byte-count.
        DefaultHttpContext context = MakeContext();
        const string Body = "日本語テスト"; // 6 chars, 18 UTF-8 bytes

        long returned = await ProxyHttp.WriteBodyUtf8Async(context, Body, TestContext.Current.CancellationToken);

        Assert.Equal(18, returned);
        Assert.Equal(18, context.Response.ContentLength);
        Assert.NotEqual(Body.Length, (int)returned); // chars != bytes — guards against char-count bug
    }

    [Fact]
    public async Task WriteBodyUtf8Async_EmptyBody_SetsZeroContentLength()
    {
        DefaultHttpContext context = MakeContext();

        long returned = await ProxyHttp.WriteBodyUtf8Async(context, string.Empty, TestContext.Current.CancellationToken);

        Assert.Equal(0, returned);
        Assert.Equal(0, context.Response.ContentLength);
    }

    [Fact]
    public async Task WriteBodyUtf8Async_Does_Not_Set_ContentLength_For204()
    {
        // RFC 9110 §15.3.5: 204 No Content must not include a body or Content-Length.
        DefaultHttpContext context = MakeContext();
        context.Response.StatusCode = StatusCodes.Status204NoContent;

        await ProxyHttp.WriteBodyUtf8Async(context, "ignored", TestContext.Current.CancellationToken);

        Assert.Null(context.Response.ContentLength);
    }

    [Fact]
    public async Task WriteBodyUtf8Async_ActualBytesWritten_MatchReturnedCount()
    {
        // Verifies that the Content-Length advertised matches the bytes actually flushed
        // to the response body — critical for HTTP/1.1 framing correctness.
        DefaultHttpContext context = MakeContext();
        const string Body = "Héllo wörld";  // contains multi-byte characters
        int expected = Encoding.UTF8.GetByteCount(Body);

        long returned = await ProxyHttp.WriteBodyUtf8Async(context, Body, TestContext.Current.CancellationToken);

        // Read actual bytes from the response stream.
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        byte[] actual = await new BinaryReader(context.Response.Body).BaseStream
            .ReadByteArrayAsync(context.Response.Body);

        Assert.Equal(expected, returned);
        Assert.Equal(expected, actual.Length);
        Assert.Equal(Body, Encoding.UTF8.GetString(actual));
    }

    [Fact]
    public async Task WriteJsonResponseAsync_SetsStatusAndContentType()
    {
        DefaultHttpContext context = MakeContext();
        const string Body = """{"test":true}""";

        long returned = await ProxyHttp.WriteJsonResponseAsync(
            context, System.Net.HttpStatusCode.OK, Body, TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("application/json", context.Response.ContentType);
        Assert.Equal(Encoding.UTF8.GetByteCount(Body), returned);
    }

    // ── CopyStreamHeaders ─────────────────────────────────────────────────────

    [Fact]
    public void CopyStreamHeaders_CopiesContentType_WhenPresent()
    {
        DefaultHttpContext context = MakeContext();
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent("data"u8.ToArray()),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");

        ProxyHttp.CopyStreamHeaders(context, response);

        Assert.Equal("text/plain", context.Response.ContentType);
    }

    [Fact]
    public void CopyStreamHeaders_CopiesContentLength_WhenPresent()
    {
        DefaultHttpContext context = MakeContext();
        byte[] body = "data"u8.ToArray();
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(body),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");

        ProxyHttp.CopyStreamHeaders(context, response);

        Assert.Equal(body.Length, context.Response.ContentLength);
    }

    [Fact]
    public void CopyStreamHeaders_CopiesETagAndAcceptRanges()
    {
        DefaultHttpContext context = MakeContext();
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent("data"u8.ToArray()),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        response.Headers.ETag = new EntityTagHeaderValue("\"abc123\"");
        response.Headers.TryAddWithoutValidation("Accept-Ranges", "bytes");

        ProxyHttp.CopyStreamHeaders(context, response);

        Assert.Equal("\"abc123\"", context.Response.Headers.ETag);
        Assert.Equal("bytes", context.Response.Headers.AcceptRanges);
    }

    [Fact]
    public void CopyStreamHeaders_CopiesLastModified_WhenPresent()
    {
        DefaultHttpContext context = MakeContext();
        var lastModified = new DateTimeOffset(2025, 1, 15, 10, 30, 0, TimeSpan.Zero);
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent("data"u8.ToArray()),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        response.Content.Headers.LastModified = lastModified;

        ProxyHttp.CopyStreamHeaders(context, response);

        Assert.Equal(lastModified.ToString("R"), context.Response.Headers.LastModified.ToString());
    }

    [Fact]
    public void CopyStreamHeaders_DoesNotCopy_UnrelatedHeaders()
    {
        DefaultHttpContext context = MakeContext();
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent("data"u8.ToArray()),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        response.Headers.TryAddWithoutValidation("X-Custom", "secret");
        response.Headers.TryAddWithoutValidation("Server", "upstream/1.0");

        ProxyHttp.CopyStreamHeaders(context, response);

        Assert.True(string.IsNullOrEmpty(context.Response.Headers["X-Custom"].ToString()));
        Assert.True(string.IsNullOrEmpty(context.Response.Headers["Server"].ToString()));
    }

    // ── StreamResponseWithoutCachingAsync ────────────────────────────────────

    [Fact]
    public async Task StreamResponseWithoutCachingAsync_CopiesLastModified_WhenPresent()
    {
        DefaultHttpContext context = MakeContext();
        var lastModified = new DateTimeOffset(2025, 1, 15, 10, 30, 0, TimeSpan.Zero);
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent("data"u8.ToArray()),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        response.Content.Headers.LastModified = lastModified;

        await ProxyHttp.StreamResponseWithoutCachingAsync(context, response, TestContext.Current.CancellationToken);

        Assert.Equal(lastModified.ToString("R"), context.Response.Headers.LastModified.ToString());
    }

    [Fact]
    public async Task StreamResponseWithoutCachingAsync_CopiesETagAndAcceptRanges_Headers()
    {
        DefaultHttpContext context = MakeContext();
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent("data"u8.ToArray()),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        response.Headers.ETag = new EntityTagHeaderValue("\"abc123\"");
        response.Headers.TryAddWithoutValidation("Accept-Ranges", "bytes");

        await ProxyHttp.StreamResponseWithoutCachingAsync(context, response, TestContext.Current.CancellationToken);

        Assert.Equal("\"abc123\"", context.Response.Headers.ETag);
        Assert.Equal("bytes", context.Response.Headers.AcceptRanges);
    }

    [Fact]
    public async Task StreamResponseWithoutCachingAsync_DoesNotCopy_UnrelatedHeaders()
    {
        DefaultHttpContext context = MakeContext();
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent("data"u8.ToArray()),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        response.Headers.TryAddWithoutValidation("X-Custom", "secret");
        response.Headers.TryAddWithoutValidation("Server", "upstream/1.0");

        await ProxyHttp.StreamResponseWithoutCachingAsync(context, response, TestContext.Current.CancellationToken);

        Assert.True(string.IsNullOrEmpty(context.Response.Headers["X-Custom"].ToString()));
        Assert.True(string.IsNullOrEmpty(context.Response.Headers["Server"].ToString()));
    }

    [Fact]
    public async Task StreamResponseWithoutCachingAsync_OmitsContentType_WhenAbsent()
    {
        DefaultHttpContext context = MakeContext();
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent("data"u8.ToArray()),
        };

        await ProxyHttp.StreamResponseWithoutCachingAsync(context, response, TestContext.Current.CancellationToken);

        Assert.Null(context.Response.ContentType);
    }

    [Fact]
    public async Task StreamResponseWithoutCachingAsync_OmitsContentLength_WhenAbsent()
    {
        DefaultHttpContext context = MakeContext();
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new NonSeekableStream(new MemoryStream("streamed"u8.ToArray()))),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");

        await ProxyHttp.StreamResponseWithoutCachingAsync(context, response, TestContext.Current.CancellationToken);

        Assert.Null(context.Response.ContentLength);
    }

    [Fact]
    public async Task StreamResponseWithoutCachingAsync_CopiesContentLength_WhenPresent()
    {
        DefaultHttpContext context = MakeContext();
        byte[] bodyBytes = "data"u8.ToArray();
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bodyBytes),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");

        await ProxyHttp.StreamResponseWithoutCachingAsync(context, response, TestContext.Current.CancellationToken);

        Assert.Equal(bodyBytes.Length, context.Response.ContentLength);
    }

    [Fact]
    public async Task StreamResponseWithoutCachingAsync_HeadRequest_DoesNotStreamBody()
    {
        DefaultHttpContext context = MakeContext();
        context.Request.Method = HttpMethods.Head;
        byte[] bodyBytes = "should-not-stream"u8.ToArray();
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bodyBytes),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        response.Content.Headers.ContentLength = bodyBytes.Length;

        await ProxyHttp.StreamResponseWithoutCachingAsync(context, response, TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(bodyBytes.Length, context.Response.ContentLength);
        Assert.Equal(0, ((MemoryStream)context.Response.Body).Length);
    }

    [Fact]
    public async Task StreamResponseWithoutCachingAsync_RequestMessageHead_DoesNotStreamBody()
    {
        DefaultHttpContext context = MakeContext();
        byte[] bodyBytes = "should-not-stream"u8.ToArray();
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bodyBytes),
            RequestMessage = new HttpRequestMessage(HttpMethod.Head, "http://upstream.example/resource"),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        response.Content.Headers.ContentLength = bodyBytes.Length;

        await ProxyHttp.StreamResponseWithoutCachingAsync(context, response, TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(bodyBytes.Length, context.Response.ContentLength);
        Assert.Equal(0, ((MemoryStream)context.Response.Body).Length);
    }

    [Fact]
    public async Task StreamResponseWithoutCachingAsync_RequestMessageGet_StreamsBody()
    {
        DefaultHttpContext context = MakeContext();
        byte[] bodyBytes = "streamed-body"u8.ToArray();
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bodyBytes),
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "http://upstream.example/resource"),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        response.Content.Headers.ContentLength = bodyBytes.Length;

        await ProxyHttp.StreamResponseWithoutCachingAsync(context, response, TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(bodyBytes, ((MemoryStream)context.Response.Body).ToArray());
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.PartialContent)]
    [InlineData(HttpStatusCode.NotModified)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task StreamResponseWithoutCachingAsync_ForwardsStatusCode(HttpStatusCode statusCode)
    {
        DefaultHttpContext context = MakeContext();
        using var response = new HttpResponseMessage(statusCode)
        {
            Content = new ByteArrayContent("data"u8.ToArray()),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");

        await ProxyHttp.StreamResponseWithoutCachingAsync(context, response, TestContext.Current.CancellationToken);

        Assert.Equal((int)statusCode, context.Response.StatusCode);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static DefaultHttpContext MakeContext()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        context.Request.Method = HttpMethods.Get;
        return context;
    }
}
