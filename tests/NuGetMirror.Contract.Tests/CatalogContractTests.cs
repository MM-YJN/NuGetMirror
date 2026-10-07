using System.Text.Json;

namespace NuGetMirror.Contract.Tests;

public sealed class CatalogContractTests(ITestOutputHelper testOutputHelper) : IAsyncDisposable
{
    private readonly ContractTestFactory _factory = new(testOutputHelper);
    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task GetCatalogIndex_RewritesPageIds()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        HttpClient client = _factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/v3/catalog0/index.json", cancellationToken);
        response.EnsureSuccessStatusCode();

        string body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.DoesNotContain("api.nuget.org", body, StringComparison.Ordinal);

        using var doc = JsonDocument.Parse(body);

        foreach (JsonElement item in doc.RootElement.GetProperty("items").EnumerateArray())
        {
            string? id = item.GetProperty("@id").GetString();
            Assert.NotNull(id);
            Assert.StartsWith("https://mirror.test/v3/catalog0/", id, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task GetCatalogPage_RewritesLeafIds()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        HttpClient client = _factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/v3/catalog0/page0.json", cancellationToken);
        response.EnsureSuccessStatusCode();

        string body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.DoesNotContain("api.nuget.org", body, StringComparison.Ordinal);

        using var doc = JsonDocument.Parse(body);

        foreach (JsonElement item in doc.RootElement.GetProperty("items").EnumerateArray())
        {
            string? id = item.GetProperty("@id").GetString();
            Assert.NotNull(id);
            Assert.StartsWith("https://mirror.test/v3/catalog0/", id, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task GetCatalogLeaf_RewritesPackageContent()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        HttpClient client = _factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/v3/catalog0/data/2025.06.01.00.00.00/newtonsoft.json.13.0.3.json", cancellationToken);
        response.EnsureSuccessStatusCode();

        string body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.DoesNotContain("api.nuget.org", body, StringComparison.Ordinal);

        using var doc = JsonDocument.Parse(body);

        string? packageContent = doc.RootElement.GetProperty("packageContent").GetString();
        Assert.NotNull(packageContent);
        Assert.StartsWith("https://mirror.test/v3-flatcontainer/", packageContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetCatalogPage_ParentIsRewritten()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        HttpClient client = _factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/v3/catalog0/page0.json", cancellationToken);
        response.EnsureSuccessStatusCode();

        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(body);

        Assert.True(doc.RootElement.TryGetProperty("parent", out JsonElement parent), "Catalog page must include a parent field.");
        string? parentUrl = parent.GetString();
        Assert.NotNull(parentUrl);
        Assert.StartsWith("https://mirror.test/v3/catalog0/", parentUrl, StringComparison.Ordinal);
    }
}
