using System.Runtime.CompilerServices;

using Microsoft.Extensions.Options;

using NuGetMirror.Configuration;

namespace NuGetMirror.Storage;

internal sealed partial class FileSystemPackageContentStore(IOptions<MirrorOptions> options, ILogger<FileSystemPackageContentStore> logger, TimeProvider timeProvider) : IPackageContentStore, ICacheMaintenance, ICacheRevalidation, IStorageHealthProbe
{
    private readonly string _root = Path.GetFullPath((options ?? throw new ArgumentNullException(nameof(options))).Value.Cache.FileSystem.Directory);
    private readonly ILogger<FileSystemPackageContentStore> _logger = logger;

    public async ValueTask<CachedContent?> TryGetAsync(string key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);

        string contentPath = KeyToPath(key);
        string metaPath = contentPath + ".meta";

        FileStream stream;
        try
        {
            stream = new FileStream(contentPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (IOException) when (!File.Exists(contentPath))
        {
            return null;
        }

        if (File.Exists(metaPath))
        {
            try
            {
                MetaInfo meta = await ReadMetaAsync(metaPath, ct).ConfigureAwait(false);
                if (meta.Length >= 0)
                {
                    return new CachedContent
                    {
                        Stream = stream,
                        Length = meta.Length,
                        ContentType = meta.ContentType,
                        ETag = meta.ETag,
                        StoredAtUtc = meta.FetchedAt,
                    };
                }
            }
            catch (Exception ex)
            {
                LogMetaReadFallback(ex, metaPath);
            }
        }

        var fileInfo = new FileInfo(contentPath);
        string contentType = GetContentTypeFromPath(contentPath);
        return new CachedContent
        {
            Stream = stream,
            Length = fileInfo.Length,
            ContentType = contentType,
        };
    }

    public ValueTask<ICacheWriteHandle> BeginWriteAsync(string key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);

        string contentPath = KeyToPath(key);
        string? dir = Path.GetDirectoryName(contentPath);

        if (dir is not null)
        {
            Directory.CreateDirectory(dir);
        }

        string tempPath = contentPath + ".tmp." + Guid.NewGuid().ToString("N");
        var handle = new FileCacheWriteHandle(tempPath, contentPath, _logger, timeProvider);
        return ValueTask.FromResult<ICacheWriteHandle>(handle);
    }

    public async IAsyncEnumerable<CacheEntryInfo> EnumerateAsync([EnumeratorCancellation] CancellationToken ct)
    {
        if (!Directory.Exists(_root))
        {
            yield break;
        }

        foreach (string filePath in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();

            ReadOnlySpan<char> fileName = Path.GetFileName(filePath.AsSpan());

            if (fileName.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) ||
                (fileName.Contains(".tmp.", StringComparison.OrdinalIgnoreCase) && fileName.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase) is false && fileName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase) is false))
            {
                continue;
            }

            string key = PathToKey(filePath);
            var fileInfo = new FileInfo(filePath);
            yield return new CacheEntryInfo(key, fileInfo.Length, fileInfo.LastWriteTimeUtc);
        }
    }

    public ValueTask DeleteAsync(string key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);

        string contentPath = KeyToPath(key);
        string metaPath = contentPath + ".meta";

        try
        {
            if (File.Exists(contentPath))
            {
                File.Delete(contentPath);
            }

            if (File.Exists(metaPath))
            {
                File.Delete(metaPath);
            }
        }
        catch (Exception ex)
        {
            LogDeleteFailed(ex, key);
            return ValueTask.CompletedTask;
        }

        PruneEmptyDirectories(contentPath);
        return ValueTask.CompletedTask;
    }

    public ValueTask TouchAsync(string key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);

        string contentPath = KeyToPath(key);

        try
        {
            if (File.Exists(contentPath))
            {
                File.SetLastWriteTimeUtc(contentPath, timeProvider.GetUtcNow().UtcDateTime);
            }
        }
        catch (Exception ex)
        {
            LogTouchFailed(ex, key);
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask RefreshTimestampAsync(string key, string contentType, string? etag, DateTimeOffset fetchedAtUtc, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);

        string contentPath = KeyToPath(key);
        string metaPath = contentPath + ".meta";

        try
        {
            MetaInfo existingMeta = await ReadMetaAsync(metaPath, ct).ConfigureAwait(false);

            var metaLines = new List<string>(4);

            if (existingMeta.Length >= 0)
            {
                metaLines.Add($"length={existingMeta.Length}");
            }

            metaLines.Add($"content-type={contentType}");
            metaLines.Add($"fetched-at={fetchedAtUtc.ToUnixTimeMilliseconds()}");

            if (etag is not null)
            {
                metaLines.Add($"etag={etag}");
            }

            await File.WriteAllLinesAsync(metaPath, metaLines, ct).ConfigureAwait(false);

            // Also update the content file's LastWriteTimeUtc so that LRU eviction
            // treats a revalidated entry as recently accessed. EnumerateAsync reads
            // LastWriteTimeUtc from the content file (not the .meta sidecar), so
            // without this touch a 304-revalidated entry looks as old as when it
            // was first written and gets evicted prematurely.
            if (File.Exists(contentPath))
            {
                File.SetLastWriteTimeUtc(contentPath, fetchedAtUtc.UtcDateTime);
            }
        }
        catch (Exception ex)
        {
            LogTouchFailed(ex, key);
        }
    }

    public ValueTask CheckAsync(CancellationToken ct)
    {
        if (!Directory.Exists(_root))
        {
            throw new InvalidOperationException($"Cache directory '{_root}' does not exist.");
        }

        return ValueTask.CompletedTask;
    }

    private string PathToKey(string filePath)
    {
        string relativePath = Path.GetRelativePath(_root, filePath);
        return relativePath.Replace(Path.DirectorySeparatorChar, '/');
    }

    private void PruneEmptyDirectories(string contentPath)
    {
        string? dir = Path.GetDirectoryName(contentPath);

        while (dir is not null && dir.Length > _root.Length)
        {
            try
            {
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    Directory.Delete(dir);
                    dir = Path.GetDirectoryName(dir);
                }
                else
                {
                    break;
                }
            }
            catch (Exception ex)
            {
                LogPruneDirectoryFailed(ex, dir);
                break;
            }
        }
    }

    private static string GetContentTypeFromPath(string path)
    {
        if (path.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase))
        {
            return "application/octet-stream";
        }

        if (path.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
        {
            return "application/xml";
        }

        return "application/octet-stream";
    }

    private string KeyToPath(string key)
    {
        string sanitizedKey = key.Replace('/', Path.DirectorySeparatorChar);

        foreach (string segment in sanitizedKey.Split(Path.DirectorySeparatorChar))
        {
            if (segment is "." or "..")
            {
                throw new ArgumentException("Key must not contain path-traversal segments.", nameof(key));
            }
        }

        string joined = Path.Join(_root, sanitizedKey);
        string fullPath = Path.GetFullPath(joined);
        string canonicalRoot = Path.GetFullPath(_root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(canonicalRoot, StringComparison.Ordinal))
        {
            throw new ArgumentException("Key resolves outside the cache root.", nameof(key));
        }

        return fullPath;
    }

    private static async Task<MetaInfo> ReadMetaAsync(string metaPath, CancellationToken ct)
    {
        string[] lines = await File.ReadAllLinesAsync(metaPath, ct).ConfigureAwait(false);
        var meta = new MetaInfo();

        foreach (string line in lines)
        {
            int eq = line.IndexOf('=', StringComparison.Ordinal);

            if (eq < 0)
            {
                continue;
            }

            string field = line[..eq];
            string value = line[(eq + 1)..];

            switch (field)
            {
                case "length":
                    if (long.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long length))
                    {
                        meta.Length = length;
                    }

                    break;

                case "content-type":
                    meta.ContentType = value;
                    break;

                case "etag":
                    meta.ETag = value;
                    break;

                case "fetched-at":
                    if (long.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long fetchedAtMs))
                    {
                        meta.FetchedAt = DateTimeOffset.FromUnixTimeMilliseconds(fetchedAtMs);
                    }

                    break;
            }
        }

        return meta;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to read metadata file {Path}; falling back to file info.")]
    private partial void LogMetaReadFallback(Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to delete cached file entry {Key}.")]
    private partial void LogDeleteFailed(Exception ex, string key);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to update cache file timestamp {Key}.")]
    private partial void LogTouchFailed(Exception ex, string key);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to prune empty cache directories {Dir}.")]
    private partial void LogPruneDirectoryFailed(Exception ex, string? dir);

    internal sealed class MetaInfo
    {
        public long Length { get; set; } = -1;
        public string ContentType { get; set; } = "application/octet-stream";
        public string? ETag { get; set; }
        public DateTimeOffset? FetchedAt { get; set; }
    }

    internal sealed partial class FileCacheWriteHandle(string tempPath, string targetPath, ILogger<FileSystemPackageContentStore> logger, TimeProvider timeProvider) : ICacheWriteHandle
    {
        private readonly string _tempPath = tempPath;
        private readonly string _targetPath = targetPath;
        private string _contentType = "application/octet-stream";
        private long _length = -1;
        private string? _etag;
        private bool _committed;

        public Stream Stream { get; } = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);

        public void SetMetadata(string contentType, long length, string? etag)
        {
            _contentType = contentType;
            _length = length;
            _etag = etag;
        }

        public async ValueTask CommitAsync(CancellationToken ct)
        {
            await Stream.FlushAsync(ct).ConfigureAwait(false);
            await Stream.DisposeAsync().ConfigureAwait(false);

            await Task.Run(() => File.Move(_tempPath, _targetPath, overwrite: true), ct).ConfigureAwait(false);

            string metaPath = _targetPath + ".meta";
            var metaLines = new List<string>(4);

            if (_length >= 0)
            {
                metaLines.Add($"length={_length}");
            }

            metaLines.Add($"content-type={_contentType}");
            metaLines.Add($"fetched-at={timeProvider.GetUtcNow().ToUnixTimeMilliseconds()}");

            if (_etag is not null)
            {
                metaLines.Add($"etag={_etag}");
            }

            await File.WriteAllLinesAsync(metaPath, metaLines, ct).ConfigureAwait(false);

            _committed = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (!_committed)
            {
                await Stream.DisposeAsync().ConfigureAwait(false);
            }

            if (!_committed)
            {
                try
                {
                    File.Delete(_tempPath);
                }
                catch (Exception ex)
                {
                    LogDeleteFailed(ex, _tempPath);
                }
            }
        }

        [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to delete temporary cache file {Path}.")]
        private partial void LogDeleteFailed(Exception ex, string path);
    }
}
