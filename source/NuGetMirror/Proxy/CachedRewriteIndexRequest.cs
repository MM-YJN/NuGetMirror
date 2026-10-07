namespace NuGetMirror.Proxy;

internal sealed record CachedRewriteIndexRequest
{
    public required string CacheKey { get; init; }

    public required string ForwardKey { get; init; }

    public required string StageLabel { get; init; }

    public required Func<HttpContext, string, Uri> BuildUpstreamUri { get; init; }

    public required Func<string, string, string> RewriteBody { get; init; }

    public required ProxyFeatureTelemetry Telemetry { get; init; }

    public required string ResourceNotAdvertisedMessage { get; init; }

    public required TimeSpan Ttl { get; init; }

    public bool LruTouch { get; init; }

    public long MaxBodyBytes { get; init; } = 256 * 1024; // overridden by caller-supplied config values

    public required string CacheContentType { get; init; }
}
