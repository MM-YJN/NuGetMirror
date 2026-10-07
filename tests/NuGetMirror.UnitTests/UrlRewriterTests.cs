using NuGetMirror.Discovery;

namespace NuGetMirror.UnitTests;

public sealed class UrlRewriterTests
{
    [Fact]
    public void Rewrite_ReplacesUpstreamPrefixWithMirrorBase()
    {
        var pairs = new List<RewritePair>
        {
            new("https://api.nuget.org/v3-flatcontainer/", "/v3-flatcontainer/"),
        };

        string body = "https://api.nuget.org/v3-flatcontainer/newtonsoft.json/index.json";
        string result = UrlRewriter.Rewrite(body, pairs, "http://localhost:5049");

        Assert.Equal("http://localhost:5049/v3-flatcontainer/newtonsoft.json/index.json", result);
    }

    [Fact]
    public void Rewrite_LeavesUntouchedUrlsAlone()
    {
        var pairs = new List<RewritePair>
        {
            new("https://api.nuget.org/v3-flatcontainer/", "/v3-flatcontainer/"),
        };

        string body = "{\"@id\":\"https://azuresearch-usnc.nuget.org/query\"}";
        string result = UrlRewriter.Rewrite(body, pairs, "http://localhost:5049");

        // No prefix matched → implementation must return the original reference
        // without allocating a new string.
        Assert.Same(body, result);
    }

    [Fact]
    public void Rewrite_PrefersLongerPrefix()
    {
        var pairs = new List<RewritePair>
        {
            new("https://api.nuget.org/", "/shorter/"),
            new("https://api.nuget.org/v3-flatcontainer/", "/v3-flatcontainer/"),
        };

        string body = "https://api.nuget.org/v3-flatcontainer/packages/";
        string result = UrlRewriter.Rewrite(body, pairs, "http://localhost:5049");

        Assert.Equal("http://localhost:5049/v3-flatcontainer/packages/", result);
    }

    [Fact]
    public void Rewrite_HandlesMultiplePrefixes()
    {
        var pairs = new List<RewritePair>
        {
            new("https://api.nuget.org/v3-flatcontainer/", "/v3-flatcontainer/"),
            new("https://api.nuget.org/v3/registration5-gz-semver2/", "/v3/registration-semver2/"),
        };

        string body = "{\"flat\":\"https://api.nuget.org/v3-flatcontainer/a/\",\"reg\":\"https://api.nuget.org/v3/registration5-gz-semver2/b/\"}";
        string result = UrlRewriter.Rewrite(body, pairs, "http://localhost:5049");

        Assert.Contains("http://localhost:5049/v3-flatcontainer/a/", result, StringComparison.Ordinal);
        Assert.Contains("http://localhost:5049/v3/registration-semver2/b/", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Rewrite_ShorterPrefixMatchesWhenLongerDoesNot()
    {
        // "https://host/v3/" matches; "https://host/v3/flat/" does NOT appear at this position.
        // The single-pass algorithm must fall back to the shorter prefix rather than skipping.
        var pairs = new List<RewritePair>
        {
            new("https://host/v3/flat/", "/v3-flatcontainer/"),
            new("https://host/v3/", "/v3/"),
        };

        string body = "\"id\":\"https://host/v3/registration/\"";
        string result = UrlRewriter.Rewrite(body, pairs, "http://mirror");

        Assert.Equal("\"id\":\"http://mirror/v3/registration/\"", result);
    }

    [Fact]
    public void Rewrite_LongerPrefixWinsWhenBothMatchAtSamePosition()
    {
        // Both "https://host/" and "https://host/v3/flat/" start at the same position.
        // The single-pass algorithm must pick the longer one.
        var pairs = new List<RewritePair>
        {
            new("https://host/", "/short/"),
            new("https://host/v3/flat/", "/v3-flatcontainer/"),
        };

        string body = "\"url\":\"https://host/v3/flat/package.nupkg\"";
        string result = UrlRewriter.Rewrite(body, pairs, "http://mirror");

        // The longer prefix wins; the shorter one must NOT produce a partial rewrite.
        Assert.Equal("\"url\":\"http://mirror/v3-flatcontainer/package.nupkg\"", result);
        Assert.DoesNotContain("http://mirror/short/", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Rewrite_ReturnsSameReferenceWhenNoPrefixMatches()
    {
        // When no upstream prefix appears in the body, the original string reference must be
        // returned (no allocation, no copy).
        var pairs = new List<RewritePair>
        {
            new("https://api.nuget.org/", "/v3/"),
        };

        string body = "{\"unrelated\":\"https://other.example.com/\"}";
        string result = UrlRewriter.Rewrite(body, pairs, "http://mirror");

        Assert.Same(body, result);
    }

    [Fact]
    public void Rewrite_PrebuiltTargetsProduceSameOutputAsBuiltInTargets()
    {
        var pairs = new List<RewritePair>
        {
            new("https://api.nuget.org/v3-flatcontainer/", "/v3-flatcontainer/"),
            new("https://api.nuget.org/v3/registration/", "/v3/registration-semver2/"),
        };

        string body = "{\"a\":\"https://api.nuget.org/v3-flatcontainer/foo\",\"b\":\"https://api.nuget.org/v3/registration/bar\"}";
        const string MirrorBase = "http://localhost:5049";

        string[] prebuilt = UrlRewriter.BuildTargets(pairs, MirrorBase);
        string withPrebuilt = UrlRewriter.Rewrite(body, pairs, MirrorBase, prebuilt);
        string withoutPrebuilt = UrlRewriter.Rewrite(body, pairs, MirrorBase);

        Assert.Equal(withoutPrebuilt, withPrebuilt);
    }

    [Fact]
    public void Rewrite_ThrowsWhenPrebuiltTargetsLengthDoesNotMatchPairsCount()
    {
        var pairs = new List<RewritePair>
        {
            new("https://api.nuget.org/v3-flatcontainer/", "/v3-flatcontainer/"),
            new("https://api.nuget.org/v3/registration/", "/v3/registration-semver2/"),
        };

        string body = "body";

        // prebuiltTargets.Length=1 but pairs.Count=2
        string[] mismatched = new string[1] { "http://mirror/v3/" };
        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            UrlRewriter.Rewrite(body, pairs, "http://mirror", mismatched));

        Assert.Contains("prebuiltTargets", ex.Message, StringComparison.Ordinal);

        // prebuiltTargets.Length=3 but pairs.Count=2
        string[] tooMany = new string[3] { "a", "b", "c" };
        Assert.Throws<ArgumentException>(() =>
            UrlRewriter.Rewrite(body, pairs, "http://mirror", tooMany));
    }

    [Fact]
    public void Rewrite_EmptyPairs_ReturnsSameReference()
    {
        // When the pair list is empty the early-return path must be taken —
        // no allocation, no copy, same reference back to the caller.
        string body = "{\"id\":\"https://api.nuget.org/v3-flatcontainer/\"}";
        string result = UrlRewriter.Rewrite(body, [], "http://mirror");

        Assert.Same(body, result);
    }

    [Fact]
    public void Rewrite_NullBody_ThrowsArgumentNullException()
    {
        var pairs = new List<RewritePair>
        {
            new("https://api.nuget.org/v3-flatcontainer/", "/v3-flatcontainer/"),
        };

        Assert.Throws<ArgumentNullException>(() =>
            UrlRewriter.Rewrite(null!, pairs, "http://localhost:5049"));
    }

    [Fact]
    public void Rewrite_NullPairs_ThrowsArgumentNullException()
    {
        const string Body = "https://api.nuget.org/v3-flatcontainer/x";

        Assert.Throws<ArgumentNullException>(() =>
            UrlRewriter.Rewrite(Body, null!, "http://localhost:5049"));
    }

    [Fact]
    public void Rewrite_NullMirrorBaseUrl_ThrowsArgumentNullException()
    {
        var pairs = new List<RewritePair>
        {
            new("https://api.nuget.org/v3-flatcontainer/", "/v3-flatcontainer/"),
        };
        const string Body = "https://api.nuget.org/v3-flatcontainer/x";

        Assert.Throws<ArgumentNullException>(() =>
            UrlRewriter.Rewrite(Body, pairs, null!));
    }

    [Fact]
    public void Rewrite_BodyEndingExactlyAtPrefix_SkipsTrailingCopy()
    {
        // The body is exactly the upstream prefix with no trailing characters.
        // After the match, spanStart == body.Length, so the final
        // `if (spanStart < body.Length)` append is skipped — the result is
        // solely the mirror target.
        var pairs = new List<RewritePair>
        {
            new("https://api.nuget.org/v3-flatcontainer/", "/v3-flatcontainer/"),
        };

        string body = "https://api.nuget.org/v3-flatcontainer/";
        string result = UrlRewriter.Rewrite(body, pairs, "http://localhost:5049");

        Assert.Equal("http://localhost:5049/v3-flatcontainer/", result);
    }

    [Fact]
    public void Rewrite_LongerPrefixFirst_ShortCircuitsShorterAndWins()
    {
        // Reverse of Rewrite_LongerPrefixWinsWhenBothMatchAtSamePosition: the longer,
        // matching prefix is FIRST. After it matches, bestLen == longer.Length, so for
        // the shorter prefix `prefix.Length > bestLen` is false and StartsWith is not
        // evaluated (short-circuit). Also proves longest-match-wins is independent of
        // pair ordering — callers need not pre-sort.
        var pairs = new List<RewritePair>
        {
            new("https://host/v3/flat/", "/v3-flatcontainer/"),
            new("https://host/", "/short/"),
        };

        string body = "\"url\":\"https://host/v3/flat/package.nupkg\"";
        string result = UrlRewriter.Rewrite(body, pairs, "http://mirror");

        Assert.Equal("\"url\":\"http://mirror/v3-flatcontainer/package.nupkg\"", result);
        Assert.DoesNotContain("http://mirror/short/", result, StringComparison.Ordinal);
    }
}
