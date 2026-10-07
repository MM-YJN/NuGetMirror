using NuGetMirror.Storage;

namespace NuGetMirror.UnitTests;

public sealed class PackageCacheKeyTests
{
    [Theory]
    [InlineData("newtonsoft.json/13.0.3/newtonsoft.json.13.0.3.nupkg", "newtonsoft.json/13.0.3/newtonsoft.json.13.0.3.nupkg")]
    [InlineData("Newtonsoft.Json/13.0.3/newtonsoft.json.13.0.3.nupkg", "newtonsoft.json/13.0.3/newtonsoft.json.13.0.3.nupkg")]
    [InlineData("packageA/1.0.0/packageA.nuspec", "packagea/1.0.0/packagea.nuspec")]
    [InlineData("Foo.Bar/2.5.0-beta1/Foo.Bar.nupkg", "foo.bar/2.5.0-beta1/foo.bar.nupkg")]
    public void TryCreate_ReturnsNormalizedKey(string path, string expectedKey)
    {
        string? key = PackageCacheKey.TryCreate(path);

        Assert.NotNull(key);
        Assert.Equal(expectedKey, key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void TryCreate_ReturnsNullForNullOrEmpty(string? path)
    {
        string? key = PackageCacheKey.TryCreate(path);

        Assert.Null(key);
    }

    // The flat-container resource also serves {id-lower}/index.json (package versions index).
    // That path flows through TryCreate at runtime and must not be cached — it is mutable.
    [Theory]
    [InlineData("newtonsoft.json/index.json")]
    [InlineData("newtonsoft.json/13.0.3/index.json")]
    public void TryCreate_ReturnsNullForFlatContainerIndexJson(string path)
    {
        string? key = PackageCacheKey.TryCreate(path);

        Assert.Null(key);
    }

    [Fact]
    public void TryCreate_ReturnsNullForUnknownExtensions()
    {
        string? key = PackageCacheKey.TryCreate("something/random.txt");

        Assert.Null(key);
    }

    [Fact]
    public void TryCreate_LeadingSlashDoesNotCrash()
    {
        string? key = PackageCacheKey.TryCreate("/newtonsoft.json/13.0.3/newtonsoft.json.13.0.3.nupkg");

        Assert.NotNull(key);
        Assert.Equal("newtonsoft.json/13.0.3/newtonsoft.json.13.0.3.nupkg", key);
    }

    [Fact]
    public void TryCreate_TreatsNuspecAsCachable()
    {
        string? key = PackageCacheKey.TryCreate("MyPackage/1.2.3/MyPackage.nuspec");

        Assert.NotNull(key);
        Assert.Contains(".nuspec", key, StringComparison.Ordinal);
    }

    // PackageFileKey: secondLastSlash < 0 — exactly one slash (id/file.nupkg, no version segment)
    [Theory]
    [InlineData("MyPackage/MyPackage.nupkg", "mypackage/mypackage.nupkg")]
    [InlineData("MyPackage/MyPackage.nuspec", "mypackage/mypackage.nuspec")]
    [InlineData("UPPER/UPPER.NUPKG", "upper/upper.nupkg")]
    public void PackageFileKey_OneSlash_ReturnsNormalizedKey(string path, string expectedKey)
    {
        string key = PackageCacheKey.PackageFileKey(path.AsSpan());

        Assert.Equal(expectedKey, key);
    }

    // PackageFileKey: lastSlash < 0 — no slash at all (bare filename)
    [Theory]
    [InlineData("File.nupkg", "file.nupkg")]
    [InlineData("FILE.NUSPEC", "file.nuspec")]
    [InlineData("MixedCase.Nupkg", "mixedcase.nupkg")]
    public void PackageFileKey_NoSlash_ReturnsNormalizedKey(string path, string expectedKey)
    {
        string key = PackageCacheKey.PackageFileKey(path.AsSpan());

        Assert.Equal(expectedKey, key);
    }

    // ── RegistrationCacheKey ─────────────────────────────────────────────────

    [Theory]
    [InlineData("semver2", "newtonsoft.json/index.json", "$registration/semver2/newtonsoft.json/index.json")]
    [InlineData("gz-semver1", "mypkg/13.0.0/index.json", "$registration/gz-semver1/mypkg/13.0.0/index.json")]
    [InlineData("semver1", "", "$registration/semver1/")]
    [InlineData("semver2", "some.package/1.0.0.json", "$registration/semver2/some.package/1.0.0.json")]
    public void RegistrationCacheKey_ReturnsCorrectStructure(string flavor, string path, string expectedKey)
    {
        string key = PackageCacheKey.RegistrationCacheKey(flavor, path);

        Assert.Equal(expectedKey, key);
    }

    [Fact]
    public void RegistrationCacheKey_NormalizesCase()
    {
        string key = PackageCacheKey.RegistrationCacheKey("SEMVER2", "Newtonsoft.Json/Index.Json");

        Assert.Equal("$registration/semver2/newtonsoft.json/index.json", key);
    }

    [Theory]
    [InlineData("/newtonsoft.json/index.json", "$registration/semver2/newtonsoft.json/index.json")]
    [InlineData("newtonsoft.json/index.json/", "$registration/semver2/newtonsoft.json/index.json")]
    [InlineData("/newtonsoft.json/index.json/", "$registration/semver2/newtonsoft.json/index.json")]
    public void RegistrationCacheKey_TrimsLeadingAndTrailingSlashes(string path, string expectedKey)
    {
        string key = PackageCacheKey.RegistrationCacheKey("semver2", path);

        Assert.Equal(expectedKey, key);
    }

    [Fact]
    public void RegistrationCacheKey_LongPath_ExercisesArrayPoolBranch()
    {
        // MaxStackAllocChars = 512; totalLen = prefix(14) + flavor(7) + 1 + pathLen
        // Path must be > 512 - 14 - 7 - 1 = 490 chars to exercise ArrayPool.
        string longSegment = new('x', 491);
        string path = $"{longSegment}/index.json";

        string key = PackageCacheKey.RegistrationCacheKey("semver2", path);

        Assert.StartsWith("$registration/semver2/", key, StringComparison.Ordinal);
        Assert.EndsWith("/index.json", key, StringComparison.Ordinal);
    }

    [Fact]
    public void RegistrationCacheKey_ThreeFlavorsProduceDistinctPrefixes()
    {
        const string PackagePath = "newtonsoft.json/index.json";

        string semver2Key = PackageCacheKey.RegistrationCacheKey("semver2", PackagePath);
        string gzSemver1Key = PackageCacheKey.RegistrationCacheKey("gz-semver1", PackagePath);
        string semver1Key = PackageCacheKey.RegistrationCacheKey("semver1", PackagePath);

        Assert.StartsWith("$registration/semver2/", semver2Key, StringComparison.Ordinal);
        Assert.StartsWith("$registration/gz-semver1/", gzSemver1Key, StringComparison.Ordinal);
        Assert.StartsWith("$registration/semver1/", semver1Key, StringComparison.Ordinal);

        Assert.NotEqual(semver2Key, gzSemver1Key);
        Assert.NotEqual(semver2Key, semver1Key);
        Assert.NotEqual(gzSemver1Key, semver1Key);
    }
}
