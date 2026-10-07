using NuGetMirror.Discovery;

namespace NuGetMirror.UnitTests;

public sealed class DiscoverySnapshotTests
{
    // ── GetOrBuildRewriteTargets: correctness ─────────────────────────────────

    [Fact]
    public void GetOrBuildRewriteTargets_TargetAtIndex_i_EqualsMirrorBase_Plus_MirrorPrefix()
    {
        var pairs = new List<RewritePair>
        {
            new("https://api.nuget.org/v3-flatcontainer/", "/v3-flatcontainer/"),
            new("https://api.nuget.org/v3/registration5-gz-semver2/", "/v3/registration-semver2/"),
        };
        DiscoverySnapshot snapshot = MakeSnapshot(pairs);
        const string MirrorBase = "https://mirror.example.com";

        string[] targets = snapshot.GetOrBuildRewriteTargets(MirrorBase);

        Assert.Equal(2, targets.Length);
        Assert.Equal(MirrorBase + "/v3-flatcontainer/", targets[0]);
        Assert.Equal(MirrorBase + "/v3/registration-semver2/", targets[1]);
    }

    [Fact]
    public void GetOrBuildRewriteTargets_EmptyPairs_ReturnsEmptyArray()
    {
        DiscoverySnapshot snapshot = MakeSnapshot([]);

        string[] targets = snapshot.GetOrBuildRewriteTargets("https://mirror.example.com");

        Assert.Empty(targets);
    }

    // ── GetOrBuildRewriteTargets: caching ────────────────────────────────────

    [Fact]
    public void GetOrBuildRewriteTargets_ReturnsSameReference_WhenMirrorBaseUnchanged()
    {
        // The array should be built once and reused on subsequent calls with the
        // same mirrorBase, so all concurrent requests share one allocation.
        DiscoverySnapshot snapshot = MakeSnapshot([new("https://api.nuget.org/", "/v3/")]);
        const string MirrorBase = "https://mirror.example.com";

        string[] t1 = snapshot.GetOrBuildRewriteTargets(MirrorBase);
        string[] t2 = snapshot.GetOrBuildRewriteTargets(MirrorBase);

        Assert.Same(t1, t2);
    }

    [Fact]
    public void GetOrBuildRewriteTargets_RebuildsArray_WhenMirrorBaseChanges()
    {
        // When no PublicBaseUrl is configured, mirrorBase changes per request.
        // The helper must produce a fresh (correct) array each time.
        DiscoverySnapshot snapshot = MakeSnapshot([new("https://api.nuget.org/", "/v3/")]);

        string[] tA = snapshot.GetOrBuildRewriteTargets("https://mirror-a.example.com");
        string[] tB = snapshot.GetOrBuildRewriteTargets("https://mirror-b.example.com");

        Assert.NotSame(tA, tB);
        Assert.Equal("https://mirror-a.example.com/v3/", tA[0]);
        Assert.Equal("https://mirror-b.example.com/v3/", tB[0]);
    }

    [Fact]
    public void GetOrBuildRewriteTargets_LastMirrorBaseWins_OnSubsequentCalls()
    {
        // Only the most-recently-used mirrorBase is cached; re-calling with an older
        // base rebuilds the array (no multi-entry LRU).
        DiscoverySnapshot snapshot = MakeSnapshot([new("https://api.nuget.org/", "/v3/")]);
        const string Base1 = "https://mirror-a.example.com";
        const string Base2 = "https://mirror-b.example.com";

        string[] t1 = snapshot.GetOrBuildRewriteTargets(Base1);
        _ = snapshot.GetOrBuildRewriteTargets(Base2); // overwrites the cache

        // Calling with Base1 again rebuilds because Base2 was cached last.
        string[] t1Again = snapshot.GetOrBuildRewriteTargets(Base1);
        Assert.NotSame(t1, t1Again); // different reference — was rebuilt
        Assert.Equal("https://mirror-a.example.com/v3/", t1Again[0]);
    }

    // ── with-expression safety (custom copy constructor) ─────────────────────

    [Fact]
    public void WithExpression_AlteringRewritePairs_DoesNotReturnStaleTargets()
    {
        // The custom copy constructor explicitly omits _cachedTargets so that a
        // 'with' expression that changes RewritePairs builds fresh targets for the
        // new pairs rather than silently returning stale ones.
        var originalPairs = new List<RewritePair>
        {
            new("https://api.nuget.org/v3-flatcontainer/", "/v3-flatcontainer/"),
        };
        DiscoverySnapshot original = MakeSnapshot(originalPairs);

        // Prime the cache for the original snapshot.
        _ = original.GetOrBuildRewriteTargets("https://mirror.example.com");

        // Create a copy with completely different RewritePairs.
        var newPairs = new List<RewritePair>
        {
            new("https://api.nuget.org/v3/registration/", "/v3/registration-semver2/"),
        };
        DiscoverySnapshot copy = original with { RewritePairs = newPairs };

        // The copy must produce targets that match the NEW pairs, not the old ones.
        string[] copyTargets = copy.GetOrBuildRewriteTargets("https://mirror.example.com");

        Assert.Single(copyTargets);
        Assert.Equal("https://mirror.example.com/v3/registration-semver2/", copyTargets[0]);
        // Make sure it does NOT return the old /v3-flatcontainer/ target.
        Assert.DoesNotContain("v3-flatcontainer", copyTargets[0], StringComparison.Ordinal);
    }

    [Fact]
    public void WithExpression_NotAlteringRewritePairs_PresentsEmptyCache()
    {
        // A 'with' expression that does NOT touch RewritePairs (e.g., only updates
        // FetchedAt) should also start with no cached targets because the custom copy
        // constructor never copies _cachedTargets.
        var pairs = new List<RewritePair>
        {
            new("https://api.nuget.org/v3-flatcontainer/", "/v3-flatcontainer/"),
        };
        DiscoverySnapshot original = MakeSnapshot(pairs);
        string[] t1 = original.GetOrBuildRewriteTargets("https://mirror.example.com");

        DiscoverySnapshot copy = original with { FetchedAt = DateTimeOffset.UtcNow.AddMinutes(1) };

        // The copy has the same RewritePairs but no cached targets, so it rebuilds
        // on first call.  The result should be equivalent but a distinct reference.
        string[] t2 = copy.GetOrBuildRewriteTargets("https://mirror.example.com");

        Assert.Equal(t1[0], t2[0]);    // same value
        Assert.NotSame(t1, t2);        // but rebuilt, not the stale reference
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static DiscoverySnapshot MakeSnapshot(IReadOnlyList<RewritePair> pairs) => new(DateTimeOffset.UtcNow, "{}", new Dictionary<string, string>(), pairs);
}
