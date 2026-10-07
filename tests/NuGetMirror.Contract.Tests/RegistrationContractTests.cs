using System.Text.Json;

using NuGetMirror.Discovery;

namespace NuGetMirror.Contract.Tests;

public sealed class RegistrationContractTests(ITestOutputHelper testOutputHelper) : IAsyncDisposable
{
    private readonly ContractTestFactory _factory = new(testOutputHelper);
    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private readonly IReadOnlyList<RewritePair> _rewritePairs =
        [
            new("https://api.nuget.org/v3/registration5-gz-semver2/", "/v3/registration-semver2/"),
            new("https://api.nuget.org/v3/catalog0/", "/v3/catalog0/"),
            new("https://api.nuget.org/v3-flatcontainer/", "/v3-flatcontainer/"),
        ];
    private readonly string _mirrorBaseUrl = "https://mirror.test";

    [Fact]
    public async Task GetRegistrationIndex_MatchesExpectedOutput()
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
    public async Task GetRegistrationPage_MatchesExpectedOutput()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        string fixturesDir = Path.Join(AppContext.BaseDirectory, "Fixtures", "Upstream");
        string upstreamJson = await File.ReadAllTextAsync(Path.Join(fixturesDir, "registration-page-newtonsoft.json"), cancellationToken);

        string expectedJson = UrlRewriter.Rewrite(upstreamJson, _rewritePairs, _mirrorBaseUrl);
        using var expectedDoc = JsonDocument.Parse(expectedJson);

        HttpClient client = _factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/v3/registration-semver2/newtonsoft.json/3.5.8.json", cancellationToken);
        response.EnsureSuccessStatusCode();

        using JsonDocument actualDoc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);

        Assert.True(JsonElement.DeepEquals(expectedDoc.RootElement, actualDoc.RootElement));
    }

    [Fact]
    public async Task GetRegistration_PackageContentUrlsAreRewritten()
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
    public async Task GetRegistration_PagedIdsAreRewritten()
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
