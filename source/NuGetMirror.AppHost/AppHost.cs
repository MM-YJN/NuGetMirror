IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(args);

// Development-only MinIO credentials for the local AppHost; never use these in production.
IResourceBuilder<ContainerResource> minio = builder.AddContainer("minio", "pgsty/minio:latest")
    .WithArgs("server", "/data", "--console-address", ":9001")
    .WithEnvironment("MINIO_ROOT_USER", "minioadmin")
    .WithEnvironment("MINIO_ROOT_PASSWORD", "minioadmin")
    .WithHttpEndpoint(9000, name: "s3")
    .WithHttpEndpoint(9001, name: "console")
    .WithVolume("nugetmirror-minio-data", "/data")
    .WithLifetime(ContainerLifetime.Persistent);

EndpointReference s3Endpoint = minio.GetEndpoint("s3");

IResourceBuilder<ContainerResource> createBucket = builder.AddContainer("minio-createbucket", "pgsty/mc:latest")
    .WithEntrypoint("/bin/sh")
    .WithArgs(
        "-c",
        "until mc alias set local $MINIO_ENDPOINT minioadmin minioadmin; do sleep 1; done && mc mb --ignore-existing local/nugetmirror")
    .WithEnvironment("MINIO_ENDPOINT", s3Endpoint)
    .WaitFor(minio);

IResourceBuilder<ProjectResource> nugetMirror = builder.AddProject<Projects.NuGetMirror>("nugetmirror")
    .WithEnvironment("Mirror__Cache__Enabled", "true")
    .WithEnvironment("Mirror__Cache__Backend", "S3")
    .WithEnvironment("Mirror__Cache__S3__Bucket", "nugetmirror")
    .WithEnvironment("Mirror__Cache__S3__Region", "us-east-1")
    .WithEnvironment("Mirror__Cache__S3__ServiceUrl", s3Endpoint)
    .WithEnvironment("Mirror__Cache__S3__AccessKey", "minioadmin")
    .WithEnvironment("Mirror__Cache__S3__SecretKey", "minioadmin")
    .WithEnvironment("Mirror__Cache__S3__UsePathStyle", "true")
    .WithEnvironment("Mirror__Cache__Eviction__Enabled", "true")
    .WithEnvironment("Mirror__Cache__Eviction__MaxAge", "12:00:00")
    .WaitForCompletion(createBucket);

builder.AddContainer("test-environment", "mcr.microsoft.com/dotnet/sdk:10.0-aot")
    .WithEntrypoint("bash")
    .WithArgs("-c", """mkdir /workspace && dotnet nuget add source $NUGET_MIRROR_URL/v3/index.json -n test-mirror --allow-insecure-connections && dotnet nuget disable source nuget.org && sleep infinity""")
    .WithEnvironment("NUGET_MIRROR_URL", nugetMirror.GetEndpoint("http"))
    .WaitFor(nugetMirror);

builder.Build().Run();
