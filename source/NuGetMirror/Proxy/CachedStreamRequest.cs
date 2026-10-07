namespace NuGetMirror.Proxy;

internal sealed record CachedStreamRequest
{
    public required string CacheKey { get; init; }

    public string ForwardKey { get; init; } = string.Empty;

    public required string StageLabel { get; init; }

    public required Func<HttpContext, string, Uri> BuildUpstreamUri { get; init; }

    public Uri? UpstreamUri { get; init; }

    public required ProxyFeatureTelemetry Telemetry { get; init; }

    public string DefaultContentType { get; init; } = "application/octet-stream";

    public long? MaxBodyBytes { get; init; }

    public bool SupportsHead { get; init; }

    public bool LruTouch { get; init; }

    public TimeSpan? Ttl { get; init; }

    public bool StaleFallbackEnabled { get; init; } = true;

    public bool UseNegativeCache { get; init; }

    public string? NegCacheKey { get; init; }

    public required string CacheContentType { get; init; }
}
