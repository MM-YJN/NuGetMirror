using System.Text.Json;

using Xunit.v3;

namespace NuGetMirror.Contract.Tests;

public sealed class SearchContractTests(ITestOutputHelper testOutputHelper) : IAsyncDisposable
{
    private readonly ContractTestFactory _factory = new(testOutputHelper);
    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task GetSearch_RewritesUpstreamUrls()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        HttpClient client = _factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/v3/search?q=newtonsoft.json&prerelease=false&semVerLevel=2.0.0", cancellationToken);
        response.EnsureSuccessStatusCode();

        string body = await response.Content.ReadAsStringAsync(cancellationToken);

        // No upstream host should leak through any URL in the search payload.
        Assert.DoesNotContain("api.nuget.org", body, StringComparison.Ordinal);

        using var doc = JsonDocument.Parse(body);

        foreach (string? name in new[] { "registration", "@id", "iconUrl" })
        {
            foreach (string value in FindPropertyValues(doc.RootElement, name))
            {
                if (value.StartsWith("http", StringComparison.Ordinal))
                {
                    Assert.StartsWith("https://mirror.test/", value, StringComparison.Ordinal);
                }
            }
        }
    }

    [Fact]
    public async Task GetSearch_RewritesNestedVersionUrls()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        HttpClient client = _factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/v3/search?q=newtonsoft.json&prerelease=false&semVerLevel=2.0.0", cancellationToken);
        response.EnsureSuccessStatusCode();

        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(body);

        JsonElement package = doc.RootElement.GetProperty("data").EnumerateArray().First();

        foreach (JsonElement version in package.GetProperty("versions").EnumerateArray())
        {
            string? id = version.GetProperty("@id").GetString();
            Assert.NotNull(id);
            Assert.StartsWith("https://mirror.test/v3/registration-semver2/", id, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task GetAutocomplete_ReturnsPackageIds()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        HttpClient client = _factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/v3/autocomplete?q=newtonsoft", cancellationToken);
        response.EnsureSuccessStatusCode();

        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(body);

        JsonElement data = doc.RootElement.GetProperty("data");
        Assert.Equal(JsonValueKind.Array, data.ValueKind);
        Assert.Contains("Newtonsoft.Json", data.EnumerateArray().Select(static e => e.GetString()));
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
