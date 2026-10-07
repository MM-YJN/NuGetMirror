using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace NuGetMirror.TestKit;

public sealed class StubUpstreamHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpResponseMessage>> _map = new(StringComparer.Ordinal);
    private readonly List<(string Prefix, Func<HttpResponseMessage> Factory)> _prefixMap = [];
    private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);

    public StubUpstreamHandler MapJson(string url, string json)
    {
        _map[url] = () => Json(json);
        return this;
    }

    /// <summary>
    /// Maps any request whose URL starts with <paramref name="urlPrefix"/> (ignoring the
    /// query string and anything after it). Useful for query-string endpoints such as the
    /// search/autocomplete services where the exact query a client sends is not known.
    /// Exact matches registered via <see cref="MapJson"/> take precedence.
    /// </summary>
    public StubUpstreamHandler MapJsonPrefix(string urlPrefix, string json)
    {
        _prefixMap.Add((urlPrefix, () => Json(json)));
        return this;
    }

    public StubUpstreamHandler MapBytes(string url, byte[] body, string contentType)
    {
        _map[url] = () => Bytes(body, contentType);
        return this;
    }

    public StubUpstreamHandler MapStatus(string url, HttpStatusCode code)
    {
        _map[url] = () => new HttpResponseMessage(code);
        return this;
    }

    public StubUpstreamHandler MapFactory(string url, Func<HttpResponseMessage> factory)
    {
        _map[url] = factory;
        return this;
    }

    public int GetCount(string url)
    {
        _counts.TryGetValue(url, out int count);
        return count;
    }

    /// <summary>
    /// Sums the number of requests whose URL starts with <paramref name="urlPrefix"/>.
    /// Useful for query-string endpoints where each request carries a different query.
    /// </summary>
    public int GetCountByPrefix(string urlPrefix)
    {
        int total = 0;

        foreach ((string? url, int count) in _counts)
        {
            if (url.StartsWith(urlPrefix, StringComparison.Ordinal))
            {
                total += count;
            }
        }

        return total;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string url = request.RequestUri?.ToString() ?? "";

        _counts.AddOrUpdate(url, 1, (_, c) => c + 1);

        if (_map.TryGetValue(url, out Func<HttpResponseMessage>? factory))
        {
            return Task.FromResult(factory());
        }

        foreach ((string? prefix, Func<HttpResponseMessage>? prefixFactory) in _prefixMap)
        {
            if (url.StartsWith(prefix, StringComparison.Ordinal))
            {
                return Task.FromResult(prefixFactory());
            }
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private static HttpResponseMessage Json(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    private static HttpResponseMessage Bytes(byte[] body, string contentType)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(body)
            {
                Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType) },
            },
        };
    }
}
