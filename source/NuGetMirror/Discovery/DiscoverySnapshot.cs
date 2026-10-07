namespace NuGetMirror.Discovery;

internal sealed record DiscoverySnapshot(
    DateTimeOffset FetchedAt,
    string RawUpstreamIndexJson,
    IReadOnlyDictionary<string, string> ForwardMap,
    IReadOnlyList<RewritePair> RewritePairs)
{
    // Lazily built and cached per mirrorBase value. When PublicBaseUrl is configured the
    // mirrorBase is constant across all requests, so targets are computed once per snapshot
    // lifetime and reused by every concurrent request — eliminating per-request string[]
    // allocation. When mirrorBase varies per request (no PublicBaseUrl) the cache is replaced
    // on every request but still removes the BuildTargets call from the rewriter's inner loop.
    // volatile ensures the reference is visible across threads without a lock; in the rare case
    // of a concurrent write two threads may both build equivalent arrays — last write wins.
    private volatile RewriteTargetsCache? _cachedTargets;

    // Custom copy constructor used by 'with' expressions.
    // Intentionally does NOT copy _cachedTargets: a 'with' expression that changes
    // RewritePairs must build fresh targets for the new pairs rather than silently
    // serving stale targets built for the old ones.
    public DiscoverySnapshot(DiscoverySnapshot original)
    {
        ArgumentNullException.ThrowIfNull(original);
        FetchedAt = original.FetchedAt;
        RawUpstreamIndexJson = original.RawUpstreamIndexJson;
        ForwardMap = original.ForwardMap;
        RewritePairs = original.RewritePairs;
        // _cachedTargets is intentionally not copied.
    }

    /// <summary>
    /// Returns a <c>string[]</c> where entry <c>i</c> is
    /// <c>mirrorBase + RewritePairs[i].MirrorPrefix</c>, built once and cached for
    /// subsequent calls with the same <paramref name="mirrorBase"/>.
    /// </summary>
    internal string[] GetOrBuildRewriteTargets(string mirrorBase)
    {
        RewriteTargetsCache? c = _cachedTargets;

        if (c is not null && string.Equals(c.MirrorBase, mirrorBase, StringComparison.Ordinal))
        {
            return c.Targets;
        }

        string[] targets = UrlRewriter.BuildTargets(RewritePairs, mirrorBase);
        _cachedTargets = new RewriteTargetsCache(mirrorBase, targets);
        return targets;
    }

    private sealed class RewriteTargetsCache(string mirrorBase, string[] targets)
    {
        public string MirrorBase { get; } = mirrorBase;
        public string[] Targets { get; } = targets;
    }
}
