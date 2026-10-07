using System.Text;

using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace NuGetMirror.E2E.Tests;

[Collection("E2E")]
public sealed class SearchEndToEndTests(KestrelMirrorFixture fixture)
{
    [Fact(Timeout = 300_000)]
    [Trait("Category", "E2E")]
    public async Task DotnetPackageSearch_ReturnsResultsFromMirror()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        string nugetConfig = CreateNugetConfig();

        await using IContainer container = await BuildAndStartContainerAsync(nugetConfig, ct);

        ExecResult result = await container.ExecAsync(
            ["dotnet", "package", "search", "testmirror.sample", "--configfile", "/work/nuget.config", "--format", "json"],
            ct);

        Assert.True(
            result.ExitCode == 0,
            $"dotnet package search failed with exit code {result.ExitCode}.\nSTDOUT:\n{result.Stdout}\nSTDERR:\n{result.Stderr}");

        // The search request must have been served through the mirror's /v3/search
        // endpoint, which forwards to the upstream search service.
        int upstreamSearchHits = fixture.GetUpstreamSearchCount();
        Assert.True(
            upstreamSearchHits >= 1,
            $"Expected the mirror to forward at least one search request upstream, but saw {upstreamSearchHits}.\nSTDOUT:\n{result.Stdout}\nSTDERR:\n{result.Stderr}");

        Assert.True(
            result.Stdout.Contains("testmirror.sample", StringComparison.OrdinalIgnoreCase),
            $"Search output did not contain the expected package.\nUpstream search hits: {upstreamSearchHits}\nSTDOUT:\n{result.Stdout}\nSTDERR:\n{result.Stderr}");
    }

    private string CreateNugetConfig()
    {
        return /* lang=xml */ $$"""
        <?xml version="1.0" encoding="utf-8"?>
        <configuration>
          <packageSources>
            <clear />
            <add key="mirror" value="http://host.testcontainers.internal:{{fixture.MirrorPort}}/v3/index.json" allowInsecureConnections="true" />
          </packageSources>
        </configuration>
        """;
    }

    private static async Task<IContainer> BuildAndStartContainerAsync(string nugetConfig, CancellationToken ct)
    {
        IContainer container = new ContainerBuilder("mcr.microsoft.com/dotnet/sdk:10.0")
            .WithEntrypoint(["sleep", "infinity"])
            .WithResourceMapping(Encoding.UTF8.GetBytes(nugetConfig), "/work/nuget.config")
            .WithWorkingDirectory("/work")
            .Build();

        await container.StartAsync(ct);
        return container;
    }
}
