using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using NuGetMirror.Configuration;

namespace NuGetMirror.UnitTests;

public sealed class MirrorOptionsValidationTests
{
    [Fact]
    public void ValidateOptions_FailsOnEmptyBucket_WhenBackendIsS3()
    {
        var services = new ServiceCollection();
        services.AddOptions<MirrorOptions>()
            .Configure(o =>
            {
                o.Cache.Backend = "S3";
                o.Cache.S3.Bucket = "";
            });
        services.AddSingleton<IValidateOptions<MirrorOptions>, MirrorOptionsValidator>();
        services.AddSingleton<IValidateOptions<MirrorOptions>, MirrorOptionsS3Validator>();
        using ServiceProvider sp = services.BuildServiceProvider();

        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(
            () => sp.GetRequiredService<IOptions<MirrorOptions>>().Value);

        Assert.Contains("Bucket", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateOptions_PassesWithMissingS3Fields_WhenBackendIsFileSystem()
    {
        var services = new ServiceCollection();
        services.AddOptions<MirrorOptions>()
            .Configure(o =>
            {
                o.Cache.Backend = "FileSystem";
                o.Cache.S3.Bucket = "";
            });
        services.AddSingleton<IValidateOptions<MirrorOptions>, MirrorOptionsValidator>();
        services.AddSingleton<IValidateOptions<MirrorOptions>, MirrorOptionsS3Validator>();
        using ServiceProvider sp = services.BuildServiceProvider();

        MirrorOptions options = sp.GetRequiredService<IOptions<MirrorOptions>>().Value;
        Assert.NotNull(options);
    }

    [Fact]
    public void ValidateOptions_FailsOnEmptyFileSystemDirectory()
    {
        var services = new ServiceCollection();
        services.AddOptions<MirrorOptions>()
            .Configure(o => o.Cache.FileSystem.Directory = "");
        services.AddSingleton<IValidateOptions<MirrorOptions>, MirrorOptionsValidator>();
        services.AddSingleton<IValidateOptions<MirrorOptions>, MirrorOptionsS3Validator>();
        using ServiceProvider sp = services.BuildServiceProvider();

        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(
            () => sp.GetRequiredService<IOptions<MirrorOptions>>().Value);

        Assert.Contains("Directory", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateOptions_FailsOnInvalidVulnerabilityMaxBodyBytes()
    {
        var services = new ServiceCollection();
        services.AddOptions<MirrorOptions>()
            .Configure(o => o.Cache.Vulnerability.MaxBodyBytes = 0);
        services.AddSingleton<IValidateOptions<MirrorOptions>, MirrorOptionsValidator>();
        services.AddSingleton<IValidateOptions<MirrorOptions>, MirrorOptionsS3Validator>();
        using ServiceProvider sp = services.BuildServiceProvider();

        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(
            () => sp.GetRequiredService<IOptions<MirrorOptions>>().Value);

        Assert.Contains("MaxBodyBytes", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateOptions_FailsOnInvalidRepositorySignaturesMaxBodyBytes()
    {
        var services = new ServiceCollection();
        services.AddOptions<MirrorOptions>()
            .Configure(o => o.Cache.RepositorySignatures.MaxBodyBytes = -1);
        services.AddSingleton<IValidateOptions<MirrorOptions>, MirrorOptionsValidator>();
        services.AddSingleton<IValidateOptions<MirrorOptions>, MirrorOptionsS3Validator>();
        using ServiceProvider sp = services.BuildServiceProvider();

        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(
            () => sp.GetRequiredService<IOptions<MirrorOptions>>().Value);

        Assert.Contains("MaxBodyBytes", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateOptions_FailsOnInvalidRegistrationMaxBodyBytes()
    {
        var services = new ServiceCollection();
        services.AddOptions<MirrorOptions>()
            .Configure(o => o.Cache.Registration.MaxBodyBytes = 0);
        services.AddSingleton<IValidateOptions<MirrorOptions>, MirrorOptionsValidator>();
        services.AddSingleton<IValidateOptions<MirrorOptions>, MirrorOptionsS3Validator>();
        using ServiceProvider sp = services.BuildServiceProvider();

        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(
            () => sp.GetRequiredService<IOptions<MirrorOptions>>().Value);

        Assert.Contains("MaxBodyBytes", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateOptions_FailsWithEmptyCredentials_WhenBackendIsS3()
    {
        var services = new ServiceCollection();
        services.AddOptions<MirrorOptions>()
            .Configure(o =>
            {
                o.Cache.Backend = "S3";
                o.Cache.S3.Bucket = "my-bucket";
                o.Cache.S3.AccessKey = "";
                o.Cache.S3.SecretKey = "";
            });
        services.AddSingleton<IValidateOptions<MirrorOptions>, MirrorOptionsValidator>();
        services.AddSingleton<IValidateOptions<MirrorOptions>, MirrorOptionsS3Validator>();
        using ServiceProvider sp = services.BuildServiceProvider();

        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(
            () => sp.GetRequiredService<IOptions<MirrorOptions>>().Value);

        Assert.Contains("AccessKey", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SecretKey", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateOptions_PassesOnValidConfig()
    {
        var services = new ServiceCollection();
        services.AddOptions<MirrorOptions>()
            .Configure(o =>
            {
                o.Cache.Backend = "S3";
                o.Cache.S3.Bucket = "my-bucket";
                o.Cache.S3.AccessKey = "my-key";
                o.Cache.S3.SecretKey = "my-secret";
                o.Cache.Vulnerability.MaxBodyBytes = 33554432;
                o.Cache.RepositorySignatures.MaxBodyBytes = 4194304;
                o.Cache.Registration.MaxBodyBytes = 67108864;
            });
        services.AddSingleton<IValidateOptions<MirrorOptions>, MirrorOptionsValidator>();
        services.AddSingleton<IValidateOptions<MirrorOptions>, MirrorOptionsS3Validator>();
        using ServiceProvider sp = services.BuildServiceProvider();

        MirrorOptions options = sp.GetRequiredService<IOptions<MirrorOptions>>().Value;
        Assert.NotNull(options);
    }

    [Fact]
    public void ValidateOptions_PassesOnDefaultUpstreamConfig()
    {
        var services = new ServiceCollection();
        services.AddOptions<MirrorOptions>()
            .Configure(o => { });
        services.AddSingleton<IValidateOptions<MirrorOptions>, MirrorOptionsValidator>();
        services.AddSingleton<IValidateOptions<MirrorOptions>, MirrorOptionsUpstreamValidator>();
        using ServiceProvider sp = services.BuildServiceProvider();

        MirrorOptions options = sp.GetRequiredService<IOptions<MirrorOptions>>().Value;
        Assert.NotNull(options);
    }

    [Fact]
    public void ValidateOptions_FailsOnEmptyIndexUrl()
    {
        var services = new ServiceCollection();
        services.AddOptions<MirrorOptions>()
            .Configure(o => o.Upstream.IndexUrl = "");
        services.AddSingleton<IValidateOptions<MirrorOptions>, MirrorOptionsValidator>();
        services.AddSingleton<IValidateOptions<MirrorOptions>, MirrorOptionsUpstreamValidator>();
        using ServiceProvider sp = services.BuildServiceProvider();

        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(
            () => sp.GetRequiredService<IOptions<MirrorOptions>>().Value);

        Assert.Contains("IndexUrl", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateOptions_FailsOnRelativeIndexUrl()
    {
        var services = new ServiceCollection();
        services.AddOptions<MirrorOptions>()
            .Configure(o => o.Upstream.IndexUrl = "relative/path");
        services.AddSingleton<IValidateOptions<MirrorOptions>, MirrorOptionsValidator>();
        services.AddSingleton<IValidateOptions<MirrorOptions>, MirrorOptionsUpstreamValidator>();
        using ServiceProvider sp = services.BuildServiceProvider();

        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(
            () => sp.GetRequiredService<IOptions<MirrorOptions>>().Value);

        Assert.Contains("IndexUrl", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateOptions_FailsOnNonHttpIndexUrl()
    {
        var services = new ServiceCollection();
        services.AddOptions<MirrorOptions>()
            .Configure(o => o.Upstream.IndexUrl = "ftp://nuget.org/v3/index.json");
        services.AddSingleton<IValidateOptions<MirrorOptions>, MirrorOptionsValidator>();
        services.AddSingleton<IValidateOptions<MirrorOptions>, MirrorOptionsUpstreamValidator>();
        using ServiceProvider sp = services.BuildServiceProvider();

        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(
            () => sp.GetRequiredService<IOptions<MirrorOptions>>().Value);

        Assert.Contains("IndexUrl", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateOptions_PassesOnValidProxyUrl()
    {
        var services = new ServiceCollection();
        services.AddOptions<MirrorOptions>()
            .Configure(o => o.Upstream.Proxy = "http://proxy.example.com:8080");
        services.AddSingleton<IValidateOptions<MirrorOptions>, MirrorOptionsValidator>();
        services.AddSingleton<IValidateOptions<MirrorOptions>, MirrorOptionsUpstreamValidator>();
        using ServiceProvider sp = services.BuildServiceProvider();

        MirrorOptions options = sp.GetRequiredService<IOptions<MirrorOptions>>().Value;
        Assert.NotNull(options);
    }

    [Fact]
    public void ValidateOptions_FailsOnInvalidProxyUrl()
    {
        var services = new ServiceCollection();
        services.AddOptions<MirrorOptions>()
            .Configure(o => o.Upstream.Proxy = "not a valid url");
        services.AddSingleton<IValidateOptions<MirrorOptions>, MirrorOptionsValidator>();
        services.AddSingleton<IValidateOptions<MirrorOptions>, MirrorOptionsUpstreamValidator>();
        using ServiceProvider sp = services.BuildServiceProvider();

        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(
            () => sp.GetRequiredService<IOptions<MirrorOptions>>().Value);

        Assert.Contains("Proxy", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateOptions_PassesOnValidPublicBaseUrl()
    {
        var services = new ServiceCollection();
        services.AddOptions<MirrorOptions>()
            .Configure(o => o.PublicBaseUrl = "https://mirror.example.com");
        services.AddSingleton<IValidateOptions<MirrorOptions>, MirrorOptionsValidator>();
        services.AddSingleton<IValidateOptions<MirrorOptions>, MirrorOptionsUpstreamValidator>();
        using ServiceProvider sp = services.BuildServiceProvider();

        MirrorOptions options = sp.GetRequiredService<IOptions<MirrorOptions>>().Value;
        Assert.NotNull(options);
    }

    [Fact]
    public void ValidateOptions_FailsOnInvalidPublicBaseUrl()
    {
        var services = new ServiceCollection();
        services.AddOptions<MirrorOptions>()
            .Configure(o => o.PublicBaseUrl = "not-a-url");
        services.AddSingleton<IValidateOptions<MirrorOptions>, MirrorOptionsValidator>();
        services.AddSingleton<IValidateOptions<MirrorOptions>, MirrorOptionsUpstreamValidator>();
        using ServiceProvider sp = services.BuildServiceProvider();

        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(
            () => sp.GetRequiredService<IOptions<MirrorOptions>>().Value);

        Assert.Contains("PublicBaseUrl", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateOptions_FailsOnNonHttpPublicBaseUrl()
    {
        var services = new ServiceCollection();
        services.AddOptions<MirrorOptions>()
            .Configure(o => o.PublicBaseUrl = "ftp://example.com");
        services.AddSingleton<IValidateOptions<MirrorOptions>, MirrorOptionsValidator>();
        services.AddSingleton<IValidateOptions<MirrorOptions>, MirrorOptionsUpstreamValidator>();
        using ServiceProvider sp = services.BuildServiceProvider();

        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(
            () => sp.GetRequiredService<IOptions<MirrorOptions>>().Value);

        Assert.Contains("PublicBaseUrl", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
