using System.Runtime.CompilerServices;

using Microsoft.Extensions.Options;

using NuGetMirror.Configuration;
using NuGetMirror.Storage.S3;

namespace NuGetMirror.Storage;

internal sealed partial class S3PackageContentStore(S3.S3Client client, IOptions<MirrorOptions> options, ILogger<S3PackageContentStore> logger) : IPackageContentStore, ICacheMaintenance, ICacheRevalidation, IStorageHealthProbe
{
    private readonly ILogger<S3PackageContentStore> _logger = logger;
    public async ValueTask<CachedContent?> TryGetAsync(string key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);

        string s3Key = BuildS3Key(key);

        try
        {
            S3GetResult? result = await client.GetObjectAsync(s3Key, ct).ConfigureAwait(false);

            if (result is null)
            {
                return null;
            }

            var stream = new WrappedStream(result);
            return new CachedContent
            {
                Stream = stream,
                Length = result.ContentLength,
                ContentType = result.ContentType,
                ETag = result.ETag,
                StoredAtUtc = result.FetchedAt,
            };
        }
        catch (S3.S3Exception ex)
        {
            LogS3FetchFailed(ex, s3Key);
            return null;
        }
        catch (HttpRequestException ex)
        {
            LogS3HttpFetchFailed(ex, s3Key);
            return null;
        }
    }

    public ValueTask<ICacheWriteHandle> BeginWriteAsync(string key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);

        string s3Key = BuildS3Key(key);
        string tempPath = Path.GetTempFileName();
        var handle = new S3CacheWriteHandle(client, tempPath, s3Key, _logger);
        return ValueTask.FromResult<ICacheWriteHandle>(handle);
    }

    public async IAsyncEnumerable<CacheEntryInfo> EnumerateAsync([EnumeratorCancellation] CancellationToken ct)
    {
        string prefix = options.Value.Cache.S3.KeyPrefix.TrimEnd('/');

        if (prefix.Length > 0)
        {
            prefix += "/";
        }

        IReadOnlyList<S3ListEntry> objects = await client.ListObjectsAsync(prefix, ct).ConfigureAwait(false);

        foreach (S3ListEntry obj in objects)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(obj.Key) || obj.Key == prefix || obj.Key.EndsWith('/'))
            {
                continue;
            }

            string key = prefix.Length > 0 && obj.Key.StartsWith(prefix, StringComparison.Ordinal)
                ? obj.Key[prefix.Length..]
                : obj.Key;

            yield return new CacheEntryInfo(key, obj.Size, obj.LastModified);
        }
    }

    public ValueTask DeleteAsync(string key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);

        string s3Key = BuildS3Key(key);
        return new ValueTask(client.DeleteObjectAsync(s3Key, ct));
    }

    public ValueTask TouchAsync(string key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);

        string s3Key = BuildS3Key(key);
        return new ValueTask(client.CopyObjectAsync(s3Key, ct));
    }

    /// <remarks>
    /// Unlike <see cref="FileSystemPackageContentStore"/>, which wraps this operation in a try-catch,
    /// this method propagates exceptions to the caller.
    /// The caller (<c>RefreshCacheTimestampAsync</c> in <c>CachedProxyPipeline.Helpers</c>) handles all
    /// exceptions via its own try-catch, treating a missed timestamp update as non-fatal, so both
    /// implementations achieve the same outcome.
    /// </remarks>
    public ValueTask RefreshTimestampAsync(string key, string contentType, string? etag, DateTimeOffset fetchedAtUtc, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);

        string s3Key = BuildS3Key(key);
        return new ValueTask(client.RevalidateObjectAsync(s3Key, contentType, etag, fetchedAtUtc, ct));
    }

    public ValueTask CheckAsync(CancellationToken ct)
        => new(client.CheckAsync(ct));

    private string BuildS3Key(string key)
    {
        if (key.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("Path traversal sequences ('..') are not allowed in cache keys.", nameof(key));
        }

        string prefix = options.Value.Cache.S3.KeyPrefix.TrimEnd('/');

        if (prefix.Length == 0)
        {
            return key.TrimStart('/');
        }

        return prefix + "/" + key.TrimStart('/');
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "S3 fetch failed for {S3Key}; returning cache miss.")]
    private partial void LogS3FetchFailed(Exception ex, string s3Key);

    [LoggerMessage(Level = LogLevel.Warning, Message = "HTTP request to S3 failed for {S3Key}; returning cache miss.")]
    private partial void LogS3HttpFetchFailed(Exception ex, string s3Key);

    private sealed class WrappedStream(S3.S3GetResult owner) : Stream
    {
        private readonly Stream _inner = owner.Stream;

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
            => _inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            => _inner.ReadAsync(buffer, ct);

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
            await owner.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                owner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed partial class S3CacheWriteHandle(S3.S3Client client, string tempPath, string s3Key, ILogger<S3PackageContentStore> logger) : ICacheWriteHandle
    {
        private string _contentType = "application/octet-stream";
        private string? _etag;

        public Stream Stream { get; } = new FileStream(tempPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);

        public void SetMetadata(string contentType, long length, string? etag)
        {
            _contentType = contentType;
            _etag = etag;
        }

        public async ValueTask CommitAsync(CancellationToken ct)
        {
            await Stream.FlushAsync(ct).ConfigureAwait(false);
            Stream.Position = 0;

            await client.PutObjectAsync(s3Key, Stream, _contentType, _etag, ct).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            await Stream.DisposeAsync().ConfigureAwait(false);

            try
            {
                File.Delete(tempPath);
            }
            catch (Exception ex)
            {
                LogFailedToDeleteTemporaryS3UploadFile(ex, tempPath);
            }
        }

        [LoggerMessage(LogLevel.Warning, "Failed to delete temporary S3 upload file {Path}.")]
        private partial void LogFailedToDeleteTemporaryS3UploadFile(Exception ex, string path);
    }
}
