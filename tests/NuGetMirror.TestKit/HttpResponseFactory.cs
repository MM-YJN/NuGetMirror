using System.Net;
using System.Net.Http.Headers;

namespace NuGetMirror.TestKit;

public static class HttpResponseFactory
{
    /// <summary>
    /// Creates a 200 OK response whose <see cref="HttpContent"/> does not expose
    /// a <c>Content-Length</c> header (wrapping the body in a
    /// <see cref="NonSeekableStream"/> so <see cref="HttpContent.Headers"/> reports
    /// <c>null</c>).
    /// </summary>
    public static HttpResponseMessage NoContentLength(byte[] body, string contentType)
    {
        var stream = new NonSeekableStream(new MemoryStream(body));
        var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    /// <summary>
    /// Creates a 200 OK response whose <c>Content-Length</c> is larger than the
    /// actual body bytes, simulating a truncated upstream response.
    /// </summary>
    public static HttpResponseMessage ShortRead(byte[] body, long declaredLength, string contentType)
    {
        var content = new ShortReadContent(body, declaredLength);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }
}
