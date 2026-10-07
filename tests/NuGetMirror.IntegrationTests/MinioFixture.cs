using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

using Microsoft.Extensions.Logging.Abstractions;

using NuGetMirror.Configuration;
using NuGetMirror.Storage;
using NuGetMirror.Storage.S3;
using NuGetMirror.TestKit.Logger;

using Xunit.Sdk;

namespace NuGetMirror.IntegrationTests;

public sealed class MinioFixture : IAsyncLifetime
{
    private const string AccessKey = "testaccesskey";
    private const string SecretKey = "testsecretkey";
    private const string BucketName = "nugetmirror";

    private IContainer? _container;

    public int Port { get; private set; }

    public bool DockerAvailable { get; private set; }

    private string Endpoint => $"http://localhost:{Port}";

    public async ValueTask InitializeAsync()
    {
        try
        {
            IContainer container = new ContainerBuilder("pgsty/minio:latest")
                .WithCommand("server", "/data")
                .WithEnvironment("MINIO_ROOT_USER", AccessKey)
                .WithEnvironment("MINIO_ROOT_PASSWORD", SecretKey)
                .WithPortBinding(0, 9000)
                .WithWaitStrategy(Wait.ForUnixContainer()
                    .UntilHttpRequestIsSucceeded(r => r.ForPath("/minio/health/live").ForPort(9000)))
                .WithLogger(new XunitDiagnosticMessageLoggerProvider().CreateLogger("testcontainers.minio"))
                .Build();

            await container.StartAsync();

            Port = container.GetMappedPublicPort(9000);
            _container = container;
        }
        catch
        {
            // Docker is unavailable or the container failed to start.
            // Mark as unavailable; each test will self-skip via Assert.Skip.
            DockerAvailable = false;
            return;
        }

        // The container is running.  Any failure here (bucket creation, SigV4 signing, …)
        // indicates a genuine defect rather than a missing Docker daemon, so let it propagate
        // and fail the test run loudly instead of silently skipping everything.
        await CreateBucketAsync();
        DockerAvailable = true;
    }

    private async Task CreateBucketAsync()
    {
        S3Client s3Client = CreateS3Client();
        await s3Client.CreateBucketAsync(CancellationToken.None);
    }

    private S3Client CreateS3Client()
    {
        var mirrorOptions = new MirrorOptions
        {
            Cache = new CacheOptions
            {
                Enabled = true,
                S3 = new S3Options
                {
                    Bucket = BucketName,
                    Region = "us-east-1",
                    ServiceUrl = Endpoint,
                    AccessKey = AccessKey,
                    SecretKey = SecretKey,
                    UsePathStyle = true,
                },
            },
        };

        var handler = new SocketsHttpHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri(Endpoint) };
        return new S3Client(httpClient,
            Microsoft.Extensions.Options.Options.Create(mirrorOptions),
            NullLogger<S3Client>.Instance,
            TimeProvider.System);
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    internal S3PackageContentStore CreateStore(string keyPrefix = "")
    {
        S3Client s3Client = CreateS3Client();
        var mirrorOptions = new MirrorOptions
        {
            Cache = new CacheOptions
            {
                Enabled = true,
                S3 = new S3Options
                {
                    Bucket = BucketName,
                    Region = "us-east-1",
                    ServiceUrl = Endpoint,
                    AccessKey = AccessKey,
                    SecretKey = SecretKey,
                    UsePathStyle = true,
                    KeyPrefix = keyPrefix,
                },
            },
        };

        return new S3PackageContentStore(s3Client, Microsoft.Extensions.Options.Options.Create(mirrorOptions), NullLogger<S3PackageContentStore>.Instance);
    }
}
