using System.Text.Json;

namespace NuGetMirror.Contract.Tests;

public sealed class ServiceIndexContractTests(ITestOutputHelper testOutputHelper) : IAsyncDisposable
{
    private readonly ContractTestFactory _factory = new(testOutputHelper);
    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task GetServiceIndex_MatchesVerifiedSnapshot()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        HttpClient client = _factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/v3/index.json", cancellationToken);
        response.EnsureSuccessStatusCode();

        string body = await response.Content.ReadAsStringAsync(cancellationToken);

        Snapshots.VerifyJson(body);
    }

    [Fact]
    public async Task GetServiceIndex_RequiredResourceUrlsAreRewritten()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        HttpClient client = _factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/v3/index.json", cancellationToken);
        response.EnsureSuccessStatusCode();

        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(body);

        var requiredTypes = new HashSet<string>
        {
            "PackageBaseAddress/3.0.0",
            "RegistrationsBaseUrl/3.6.0",
            "RegistrationsBaseUrl/3.4.0",
            "RegistrationsBaseUrl",
            "RegistrationsBaseUrl/3.0.0-rc",
            "RegistrationsBaseUrl/3.0.0-beta",
        };

        foreach (JsonElement resource in doc.RootElement.GetProperty("resources").EnumerateArray())
        {
            string? type = resource.GetProperty("@type").GetString();
            if (type is not null && requiredTypes.Contains(type))
            {
                string? id = resource.GetProperty("@id").GetString();
                Assert.NotNull(id);
                Assert.StartsWith("https://mirror.test/", id, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task GetServiceIndex_ContainsRequiredResourceTypes()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        HttpClient client = _factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/v3/index.json", cancellationToken);
        response.EnsureSuccessStatusCode();

        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(body);

        var types = new HashSet<string>();
        foreach (JsonElement resource in doc.RootElement.GetProperty("resources").EnumerateArray())
        {
            string? type = resource.GetProperty("@type").GetString();
            if (type is not null)
            {
                types.Add(type);
            }
        }

        Assert.Contains("PackageBaseAddress/3.0.0", types);
        Assert.Contains("RegistrationsBaseUrl/3.6.0", types);
        Assert.Contains("RegistrationsBaseUrl/3.4.0", types);
        Assert.Contains("RegistrationsBaseUrl", types);
    }
}
