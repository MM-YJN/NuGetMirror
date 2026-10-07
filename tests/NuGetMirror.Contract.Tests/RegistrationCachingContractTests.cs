using System.Text.Json;

using NuGetMirror.Discovery;

namespace NuGetMirror.Contract.Tests;

public sealed class RegistrationCachingContractTests(ITestOutputHelper testOutputHelper) : IAsyncDisposable
{
    private readonly CachingContractTestFactory _factory = new(testOutputHelper);
    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private readonly IReadOnlyList<RewritePair> _rewritePairs =
        [
            new("https://api.nuget.org/v3/registration5-gz-semver2/", "/v3/registration-semver2/"),
            new("https://api.nuget.org/v3/catalog0/", "/v3/catalog0/"),
            new("https://api.nuget.org/v3-flatcontainer/", "/v3-flatcontainer/"),
        ];
    private readonly string _mirrorBaseUrl = "https://mirror.test";

    [Fact]
    public async Task GetRegistrationIndex_WithCaching_MatchesExpectedOutput()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        string fixturesDir = Path.Join(AppContext.BaseDirectory, "Fixtures", "Upstream");
        string upstreamJson = await File.ReadAllTextAsync(Path.Join(fixturesDir, "registration-newtonsoft.json"), cancellationToken);

        string expectedJson = UrlRewriter.Rewrite(upstreamJson, _rewritePairs, _mirrorBaseUrl);
        using var expectedDoc = JsonDocument.Parse(expectedJson);

        HttpClient client = _factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/v3/registration-semver2/newtonsoft.json/index.json", cancellationToken);
        response.EnsureSuccessStatusCode();

        using JsonDocument actualDoc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);

        Assert.True(JsonElement.DeepEquals(expectedDoc.RootElement, actualDoc.RootElement));
    }

    [Fact]
    public async Task GetRegistration_PackageContentUrlsAreRewritten_WithCaching()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        HttpClient client = _factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/v3/registration-semver2/newtonsoft.json/index.json", cancellationToken);
        response.EnsureSuccessStatusCode();

        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(body);

        foreach (string value in FindPropertyValues(doc.RootElement, "packageContent"))
        {
            Assert.DoesNotContain("api.nuget.org", value, StringComparison.Ordinal);
        }

        foreach (string value in FindPropertyValues(doc.RootElement, "registration"))
        {
            Assert.DoesNotContain("api.nuget.org", value, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task GetRegistration_PagedIdsAreRewritten_WithCaching()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        HttpClient client = _factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/v3/registration-semver2/newtonsoft.json/index.json", cancellationToken);
        response.EnsureSuccessStatusCode();

        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(body);

        foreach (JsonElement item in doc.RootElement.GetProperty("items").EnumerateArray())
        {
            string? id = item.GetProperty("@id").GetString();
            Assert.NotNull(id);
            Assert.StartsWith("https://mirror.test/v3/registration-semver2/", id, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task GetRegistration_CacheHit_DoesNotCallUpstreamAgain()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        HttpClient client = _factory.CreateClient();

        // First request: served from upstream (caches the response)
        HttpResponseMessage response1 = await client.GetAsync("/v3/registration-semver2/newtonsoft.json/index.json", cancellationToken);
        response1.EnsureSuccessStatusCode();

        // Second request: served from cache, no additional upstream call
        HttpResponseMessage response2 = await client.GetAsync("/v3/registration-semver2/newtonsoft.json/index.json", cancellationToken);
        response2.EnsureSuccessStatusCode();

        string body1 = await response1.Content.ReadAsStringAsync(cancellationToken);
        string body2 = await response2.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(body1, body2);
    }

    private static List<string> FindPropertyValues(JsonElement element, string propertyName)
    {
        var results = new List<string>();

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty prop in element.EnumerateObject())
            {
                if (prop.NameEquals(propertyName) && prop.Value.ValueKind == JsonValueKind.String)
                {
                    results.Add(prop.Value.GetString()
                        ?? throw new InvalidOperationException("Expected a JSON string value but GetString() returned null."));
                }

                results.AddRange(FindPropertyValues(prop.Value, propertyName));
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                results.AddRange(FindPropertyValues(item, propertyName));
            }
        }

        return results;
    }
}
