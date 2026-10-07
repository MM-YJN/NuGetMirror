namespace NuGetMirror.Contract.Tests;

public sealed class FlatContainerContractTests(ITestOutputHelper testOutputHelper) : IAsyncDisposable
{
    private readonly ContractTestFactory _factory = new(testOutputHelper);
    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task GetFlatContainerIndex_MatchesVerifiedSnapshot()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        HttpClient client = _factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/v3-flatcontainer/newtonsoft.json/index.json", cancellationToken);
        response.EnsureSuccessStatusCode();

        string body = await response.Content.ReadAsStringAsync(cancellationToken);

        Snapshots.VerifyJson(body);
    }

    [Fact]
    public async Task GetFlatContainerIndex_PassthroughVersionsList()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        HttpClient client = _factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync("/v3-flatcontainer/newtonsoft.json/index.json", cancellationToken);
        response.EnsureSuccessStatusCode();

        string body = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Contains("\"versions\"", body, StringComparison.Ordinal);
    }
}
