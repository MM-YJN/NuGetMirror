using System.Text;

using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace NuGetMirror.E2E.Tests;

[Collection("E2E")]
public sealed class RestoreEndToEndTests(KestrelMirrorFixture fixture)
{
    [Fact(Timeout = 300_000)]
    [Trait("Category", "E2E")]
    public async Task DotnetRestore_SucceedsWithMirrorSource()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        string csproj = CreateCsproj();
        string nugetConfig = CreateNugetConfig();

        await using IContainer container = await BuildAndStartContainerAsync(csproj, nugetConfig, ct);

        ExecResult result = await container.ExecAsync(
            ["dotnet", "restore", "/work/consumer.csproj", "--configfile", "/work/nuget.config", "--no-cache"],
            ct);

        Assert.True(
            result.ExitCode == 0,
            $"dotnet restore failed with exit code {result.ExitCode}.\nSTDOUT:\n{result.Stdout}\nSTDERR:\n{result.Stderr}");

        ExecResult checkResult = await container.ExecAsync(
            ["test", "-f", "/work/packages/testmirror.sample/1.0.0/testmirror.sample.1.0.0.nupkg"],
            ct);

        Assert.True(
            checkResult.ExitCode == 0,
            $"Expected nupkg not found.\nSTDOUT:\n{checkResult.Stdout}\nSTDERR:\n{checkResult.Stderr}");
    }

    [Fact(Timeout = 300_000)]
    [Trait("Category", "E2E")]
    public async Task DotnetRestore_ServesFromCacheOnSecondRun()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        string csproj = CreateCsproj();
        string nugetConfig = CreateNugetConfig();

        await using IContainer container = await BuildAndStartContainerAsync(csproj, nugetConfig, ct);

        ExecResult result1 = await container.ExecAsync(
            ["dotnet", "restore", "/work/consumer.csproj", "--configfile", "/work/nuget.config", "--no-cache"],
            ct);

        Assert.True(
            result1.ExitCode == 0,
            $"First restore failed.\nSTDOUT:\n{result1.Stdout}\nSTDERR:\n{result1.Stderr}");

        string nupkgUrl = "https://api.nuget.org/v3-flatcontainer/testmirror.sample/1.0.0/testmirror.sample.1.0.0.nupkg";
        Assert.Equal(1, fixture.GetUpstreamCount(nupkgUrl));

        ExecResult clearResult = await container.ExecAsync(
            ["rm", "-rf", "/work/packages"],
            ct);

        Assert.True(clearResult.ExitCode == 0, $"Failed to clear packages folder.\nSTDERR:\n{clearResult.Stderr}");

        ExecResult result2 = await container.ExecAsync(
            ["dotnet", "restore", "/work/consumer.csproj", "--configfile", "/work/nuget.config", "--no-cache"],
            ct);

        Assert.True(
            result2.ExitCode == 0,
            $"Second restore failed.\nSTDOUT:\n{result2.Stdout}\nSTDERR:\n{result2.Stderr}");

        Assert.Equal(1, fixture.GetUpstreamCount(nupkgUrl));

        ExecResult checkResult = await container.ExecAsync(
            ["test", "-f", "/work/packages/testmirror.sample/1.0.0/testmirror.sample.1.0.0.nupkg"],
            ct);

        Assert.True(
            checkResult.ExitCode == 0,
            $"Expected nupkg not found after second restore.\nSTDOUT:\n{checkResult.Stdout}\nSTDERR:\n{checkResult.Stderr}");
    }

    private static string CreateCsproj()
    {
        return /* lang=xml */ $$"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="testmirror.sample" Version="1.0.0" />
          </ItemGroup>
        </Project>
        """;
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
          <config>
            <add key="globalPackagesFolder" value="/work/packages" />
          </config>
        </configuration>
        """;
    }

    private static async Task<IContainer> BuildAndStartContainerAsync(string csproj, string nugetConfig, CancellationToken ct)
    {
        IContainer container = new ContainerBuilder("mcr.microsoft.com/dotnet/sdk:10.0")
            .WithEntrypoint(["sleep", "infinity"])
            .WithResourceMapping(Encoding.UTF8.GetBytes(csproj), "/work/consumer.csproj")
            .WithResourceMapping(Encoding.UTF8.GetBytes(nugetConfig), "/work/nuget.config")
            .WithWorkingDirectory("/work")
            .Build();

        await container.StartAsync(ct);
        return container;
    }
}
