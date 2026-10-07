using System.Globalization;
using System.Net;
using System.Xml;

using Microsoft.Extensions.Options;

using NuGetMirror.Configuration;

namespace NuGetMirror.Storage.S3;

internal sealed partial class S3Client(HttpClient http, IOptions<MirrorOptions> options, ILogger<S3Client> logger, TimeProvider timeProvider) : IDisposable
{
    public async Task<S3GetResult?> GetObjectAsync(string key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);

        (Uri? uri, string? host, S3Options? s3, string _) = ResolveObjectUri(key);

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("Host", host);

        var signer = new AwsSignatureV4(s3.AccessKey, s3.SecretKey, s3.Region, timeProvider);
        signer.Sign(request, AwsSignatureV4.EmptyPayloadHashHex);

        HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.NotFound)
        {
            response.Dispose();
            LogS3NotFound(key);
            return null;
        }

        try
        {
            response.EnsureSuccessStatusCode();

            LogS3GetSuccess(key, response.Content.Headers.ContentLength ?? -1L);

            long contentLength = response.Content.Headers.ContentLength ?? -1L;
            string contentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
            string? etag = ExtractMetaHeader(response, "x-amz-meta-etag");
            string? fetchedAtStr = ExtractMetaHeader(response, "x-amz-meta-fetched-at");
            DateTimeOffset? fetchedAt = null;

            if (fetchedAtStr is not null && long.TryParse(fetchedAtStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out long fetchedAtMs))
            {
                fetchedAt = DateTimeOffset.FromUnixTimeMilliseconds(fetchedAtMs);
            }

            Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return new S3GetResult(response, stream, contentLength, contentType, etag, fetchedAt);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    public async Task PutObjectAsync(string key, Stream content, string contentType, string? etag, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);

        (Uri? uri, string? host, S3Options? s3, string _) = ResolveObjectUri(key);

        string payloadHash = AwsSignatureV4.ComputePayloadHashHex(content);

        using var request = new HttpRequestMessage(HttpMethod.Put, uri);
        request.Headers.TryAddWithoutValidation("Host", host);

        request.Content = new StreamContent(content);
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);

        AddObjectMetadata(request, etag, timeProvider.GetUtcNow());

        var signer = new AwsSignatureV4(s3.AccessKey, s3.SecretKey, s3.Region, timeProvider);
        signer.Sign(request, payloadHash);

        await SendExpectingStatusAsync(request, HttpStatusCode.OK, HttpStatusCode.Created, () => LogS3PutSuccess(key), ct).ConfigureAwait(false);
    }

    public async Task CreateBucketAsync(CancellationToken ct)
    {
        S3Options s3 = options.Value.Cache.S3;
        string host = GetHost(s3);
        string baseUrl = GetBaseUrl(s3);

        using var request = new HttpRequestMessage(HttpMethod.Put, baseUrl + "/");
        request.Headers.TryAddWithoutValidation("Host", host);

        var signer = new AwsSignatureV4(s3.AccessKey, s3.SecretKey, s3.Region, timeProvider);
        signer.Sign(request, AwsSignatureV4.EmptyPayloadHashHex);

        await SendExpectingStatusAsync(request, HttpStatusCode.OK, HttpStatusCode.Conflict, LogS3CreateBucketSuccess, ct).ConfigureAwait(false);
    }

    public async Task CheckAsync(CancellationToken ct)
    {
        S3Options s3 = options.Value.Cache.S3;
        string baseUrl = GetBaseUrl(s3);
        string host = GetHost(s3);

        var uri = new Uri(baseUrl + "?max-keys=1");

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("Host", host);

        var signer = new AwsSignatureV4(s3.AccessKey, s3.SecretKey, s3.Region, timeProvider);
        signer.Sign(request, AwsSignatureV4.EmptyPayloadHashHex);

        using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new S3Exception(response.StatusCode, body);
        }
    }

    public void Dispose() => http.Dispose();

    public async Task<IReadOnlyList<S3ListEntry>> ListObjectsAsync(string prefix, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(prefix);

        S3Options s3 = options.Value.Cache.S3;
        string baseUrl = GetBaseUrl(s3);
        string host = GetHost(s3);
        var results = new List<S3ListEntry>();
        string? continuationToken = null;

        do
        {
            string queryString = "?list-type=2&prefix=" + Uri.EscapeDataString(prefix);

            if (continuationToken is not null)
            {
                queryString += "&continuation-token=" + Uri.EscapeDataString(continuationToken);
            }

            var uri = new Uri(baseUrl + queryString);

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("Host", host);

            var signer = new AwsSignatureV4(s3.AccessKey, s3.SecretKey, s3.Region, timeProvider);
            signer.Sign(request, AwsSignatureV4.EmptyPayloadHashHex);

            using HttpResponseMessage response = await http.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            string xml = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            (IReadOnlyList<S3ListEntry>? entries, continuationToken) = ParseListObjectsResult(xml);
            results.AddRange(entries);
        }
        while (continuationToken is not null);

        LogS3ListSuccess(prefix, results.Count);
        return results;
    }

    public async Task DeleteObjectAsync(string key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);

        (Uri? uri, string? host, S3Options? s3, string _) = ResolveObjectUri(key);

        using var request = new HttpRequestMessage(HttpMethod.Delete, uri);
        request.Headers.TryAddWithoutValidation("Host", host);

        var signer = new AwsSignatureV4(s3.AccessKey, s3.SecretKey, s3.Region, timeProvider);
        signer.Sign(request, AwsSignatureV4.EmptyPayloadHashHex);

        await SendExpectingStatusAsync(request, HttpStatusCode.NoContent, HttpStatusCode.NotFound, () => LogS3DeleteSuccess(key), ct).ConfigureAwait(false);
    }

    public async Task CopyObjectAsync(string key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);

        // Retrieve the current object metadata so it can be preserved through the copy.
        // A self-copy with x-amz-metadata-directive: COPY is rejected by AWS S3 because the
        // source and destination are identical and nothing changes. REPLACE is required, but
        // REPLACE blanks every metadata header that is not explicitly re-supplied, which would
        // drop the cached ETag and fetch timestamp. HEAD the object first, then re-supply them
        // so the cache entry stays valid after the LRU touch.
        S3ObjectMeta? meta = await HeadObjectAsync(key, ct).ConfigureAwait(false);

        if (meta is null)
        {
            // Object no longer exists; nothing to touch.
            return;
        }

        using HttpRequestMessage request = BuildSelfCopyRequest(key, meta.ContentType);

        if (meta.ETag is not null)
        {
            request.Headers.TryAddWithoutValidation("x-amz-meta-etag", meta.ETag);
        }

        if (meta.FetchedAt is { } fetchedAt)
        {
            request.Headers.TryAddWithoutValidation("x-amz-meta-fetched-at", fetchedAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
        }

        await SendSignedCopyAsync(request, key, ct).ConfigureAwait(false);
    }

    public async Task RevalidateObjectAsync(string key, string contentType, string? etag, DateTimeOffset fetchedAtUtc, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);
        using HttpRequestMessage request = BuildSelfCopyRequest(key, contentType);

        AddObjectMetadata(request, etag, fetchedAtUtc);

        await SendSignedCopyAsync(request, key, ct).ConfigureAwait(false);
    }

    private (Uri Uri, string Host, S3Options S3, string EscapedKey) ResolveObjectUri(string key)
    {
        S3Options s3 = options.Value.Cache.S3;
        string baseUrl = GetBaseUrl(s3);
        string escapedKey = EscapeKey(key);
        var uri = new Uri($"{baseUrl}/{escapedKey}");
        string host = GetHost(s3);
        return (uri, host, s3, escapedKey);
    }

    // Returns the content-type, ETag, and optional fetched-at timestamp of an existing
    // object, or null if the object does not exist. Used by CopyObjectAsync to preserve
    // metadata through the REPLACE self-copy that refreshes LastModified for LRU eviction.
    private async Task<S3ObjectMeta?> HeadObjectAsync(string key, CancellationToken ct)
    {
        (Uri? uri, string? host, S3Options? s3, string _) = ResolveObjectUri(key);

        using var request = new HttpRequestMessage(HttpMethod.Head, uri);
        request.Headers.TryAddWithoutValidation("Host", host);

        var signer = new AwsSignatureV4(s3.AccessKey, s3.SecretKey, s3.Region, timeProvider);
        signer.Sign(request, AwsSignatureV4.EmptyPayloadHashHex);

        using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        string contentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
        string? etag = ExtractMetaHeader(response, "x-amz-meta-etag");
        string? fetchedAtStr = ExtractMetaHeader(response, "x-amz-meta-fetched-at");
        DateTimeOffset? fetchedAt = null;

        if (fetchedAtStr is not null && long.TryParse(fetchedAtStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out long fetchedAtMs))
        {
            fetchedAt = DateTimeOffset.FromUnixTimeMilliseconds(fetchedAtMs);
        }

        return new S3ObjectMeta(contentType, etag, fetchedAt);
    }

    private static void AddObjectMetadata(HttpRequestMessage request, string? etag, DateTimeOffset fetchedAtUtc)
    {
        if (etag is not null)
        {
            request.Headers.TryAddWithoutValidation("x-amz-meta-etag", etag);
        }

        request.Headers.TryAddWithoutValidation("x-amz-meta-fetched-at", fetchedAtUtc.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
    }

    private HttpRequestMessage BuildSelfCopyRequest(string key, string contentType)
    {
        // Self-copy with REPLACE resets metadata; re-supply Content-Type so it isn't blanked.
        (Uri? uri, string? host, S3Options? s3, string? escapedKey) = ResolveObjectUri(key);
        string source = $"/{s3.Bucket}/{escapedKey}";

        var request = new HttpRequestMessage(HttpMethod.Put, uri);
        request.Headers.TryAddWithoutValidation("Host", host);
        request.Headers.TryAddWithoutValidation("x-amz-copy-source", source);
        request.Headers.TryAddWithoutValidation("x-amz-metadata-directive", "REPLACE");

        request.Content = new StringContent(string.Empty);
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);

        return request;
    }

    private async Task SendSignedCopyAsync(HttpRequestMessage request, string key, CancellationToken ct)
    {
        S3Options s3 = options.Value.Cache.S3;
        var signer = new AwsSignatureV4(s3.AccessKey, s3.SecretKey, s3.Region, timeProvider);
        string payloadHash = AwsSignatureV4.ComputePayloadHashHex(Stream.Null);
        signer.Sign(request, payloadHash);

        await SendExpectingStatusAsync(request, HttpStatusCode.OK, HttpStatusCode.NoContent, () => LogS3CopySuccess(key), ct).ConfigureAwait(false);
    }

    private async Task SendExpectingStatusAsync(
        HttpRequestMessage request,
        HttpStatusCode success1,
        HttpStatusCode success2,
        Action logSuccess,
        CancellationToken ct)
    {
        using HttpResponseMessage response = await http.SendAsync(request, ct).ConfigureAwait(false);

        if (response.StatusCode == success1 || response.StatusCode == success2)
        {
            logSuccess();
            return;
        }

        string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        throw new S3Exception(response.StatusCode, body);
    }

    private static (IReadOnlyList<S3ListEntry> Entries, string? NextContinuationToken) ParseListObjectsResult(string xml)
    {
        var entries = new List<S3ListEntry>();
        string? nextToken = null;

        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { IgnoreWhitespace = true, Async = false });

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            if (reader.Name == "Contents")
            {
                S3ListEntry entry = ParseListEntry(reader);
                entries.Add(entry);
            }
            else if (reader.Name == "NextContinuationToken")
            {
                reader.Read();
                nextToken = reader.Value;
            }
        }

        return (entries, nextToken);
    }

    private static S3ListEntry ParseListEntry(XmlReader reader)
    {
        string? key = null;
        long size = 0;
        DateTimeOffset lastModified = default;

        int depth = reader.Depth;
        while (reader.Read() && (reader.Depth > depth || reader.NodeType != XmlNodeType.EndElement))
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            switch (reader.Name)
            {
                case "Key":
                    reader.Read();
                    key = reader.Value;
                    break;

                case "Size":
                    reader.Read();
                    _ = long.TryParse(reader.Value, out size);
                    break;

                case "LastModified":
                    reader.Read();
                    _ = DateTimeOffset.TryParse(reader.Value, out lastModified);
                    break;
            }
        }

        return new S3ListEntry(key ?? "", size, lastModified);
    }

    private static string GetBaseUrl(S3Options s3)
    {
        if (s3.UsePathStyle)
        {
            string endpoint = !string.IsNullOrEmpty(s3.ServiceUrl)
                ? s3.ServiceUrl.TrimEnd('/')
                : $"https://s3.{s3.Region}.amazonaws.com";
            return $"{endpoint}/{s3.Bucket}";
        }

        if (!string.IsNullOrEmpty(s3.ServiceUrl))
        {
            string host = new Uri(s3.ServiceUrl).Authority;
            return $"https://{s3.Bucket}.{host}";
        }

        return $"https://{s3.Bucket}.s3.{s3.Region}.amazonaws.com";
    }

    private static string GetHost(S3Options s3)
    {
        if (s3.UsePathStyle)
        {
            string endpoint = !string.IsNullOrEmpty(s3.ServiceUrl)
                ? s3.ServiceUrl
                : $"https://s3.{s3.Region}.amazonaws.com";
            return new Uri(endpoint).Authority;
        }

        if (!string.IsNullOrEmpty(s3.ServiceUrl))
        {
            string host = new Uri(s3.ServiceUrl).Authority;
            return $"{s3.Bucket}.{host}";
        }

        return $"{s3.Bucket}.s3.{s3.Region}.amazonaws.com";
    }

    private static string EscapeKey(string key) => AwsSignatureV4.EncodePathSegment(key);

    private static string? ExtractMetaHeader(HttpResponseMessage response, string name)
    {
        string headerName = name.ToLowerInvariant();
        foreach ((string? key, IEnumerable<string>? values) in response.Headers)
        {
            if (string.Equals(key, headerName, StringComparison.OrdinalIgnoreCase))
            {
                return values.FirstOrDefault();
            }
        }

        return null;
    }

    private sealed record S3ObjectMeta(string ContentType, string? ETag, DateTimeOffset? FetchedAt);

    [LoggerMessage(Level = LogLevel.Debug, Message = "S3 get {Key} returned {Length} bytes")]
    private partial void LogS3GetSuccess(string key, long length);

    [LoggerMessage(Level = LogLevel.Debug, Message = "S3 get {Key}: not found")]
    private partial void LogS3NotFound(string key);

    [LoggerMessage(Level = LogLevel.Debug, Message = "S3 put {Key} succeeded")]
    private partial void LogS3PutSuccess(string key);

    [LoggerMessage(Level = LogLevel.Debug, Message = "S3 delete {Key} succeeded")]
    private partial void LogS3DeleteSuccess(string key);

    [LoggerMessage(Level = LogLevel.Debug, Message = "S3 copy {Key} succeeded")]
    private partial void LogS3CopySuccess(string key);

    [LoggerMessage(Level = LogLevel.Debug, Message = "S3 list {Prefix} returned {Count} objects")]
    private partial void LogS3ListSuccess(string prefix, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "S3 bucket created or already exists")]
    private partial void LogS3CreateBucketSuccess();
}
