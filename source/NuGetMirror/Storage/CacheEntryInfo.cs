namespace NuGetMirror.Storage;

internal readonly record struct CacheEntryInfo(string Key, long Length, DateTimeOffset LastModifiedUtc);
