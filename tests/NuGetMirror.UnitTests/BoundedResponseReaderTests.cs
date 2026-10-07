using System.Net;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NuGetMirror.Configuration;
using NuGetMirror.Diagnostics;
using NuGetMirror.Discovery;
using NuGetMirror.TestKit;
using NuGetMirror.Upstream;

namespace NuGetMirror.UnitTests;

public sealed class BoundedResponseReaderTests
{
    [Fact]
    public async Task ReadAsync_CancellationReleasesStream()
    {
        using var cancellation = new CancellationTokenSource();
        using var stream = new GeneratedBodyStream(long.MaxValue);
        using var content = new StreamContent(stream);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BoundedResponseReader.ReadAsync(content, 32, cancellation.Token));
        Assert.True(stream.Disposed);
        Assert.Equal(0, stream.BytesRead);
    }

    [Theory]
    [InlineData(null, 1000, 33)]
    [InlineData(1L, 1000, 33)]
    [InlineData(long.MaxValue, 1000, 0)]
    [InlineData(null, 32, 32)]
    [InlineData(32L, 32, 32)]
    [InlineData(null, 0, 0)]
    public async Task ReadAsync_BoundsConsumption(long? declared, long actual, long expectedRead)
    {
        using var stream = new GeneratedBodyStream(actual);
        using (var content = new GeneratedBodyContent(stream))
        {
            content.Headers.ContentLength = declared;
            byte[]? result = await BoundedResponseReader.ReadAsync(content, 32, TestContext.Current.CancellationToken);
            Assert.Equal(expectedRead, stream.BytesRead);
            Assert.Equal(declared > 32 ? 0 : 1, content.StreamsOpened);
            if (actual > 32)
            {
                Assert.Null(result);
            }
            else
            {
                Assert.NotNull(result);
                Assert.Equal(actual, result.LongLength);
            }

            if (expectedRead > 0)
            {
                Assert.True(stream.Disposed);
            }
        }

        Assert.True(stream.Disposed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetAsync_BothPoliciesReturnWithoutReading(bool streamPolicy)
    {
        using var stream = new GeneratedBodyStream(long.MaxValue);
        using StubUpstreamHandler handler = new StubUpstreamHandler().MapFactory("https://example.com/index.json",
            () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new GeneratedBodyContent(stream) });
        using var metrics = new MirrorMetrics();
        var client = new UpstreamClient(new TestHttpClientFactory(handler), Options.Create(new MirrorOptions()),
            NullLogger<UpstreamClient>.Instance, metrics);
        using (HttpResponseMessage response = await client.GetAsync(new Uri("https://example.com/index.json"), streamPolicy, TestContext.Current.CancellationToken))
        {
            Assert.Equal(0, stream.BytesRead);
            Assert.False(stream.Disposed);
        }

        Assert.True(stream.Disposed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReadAsync_DisposesOnFailure(bool canceled)
    {
        using var stream = new GeneratedBodyStream(100) { ReadFailure = canceled ? new OperationCanceledException() : new IOException() };
        using var content = new StreamContent(stream);
        if (canceled)
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                BoundedResponseReader.ReadAsync(content, 32, TestContext.Current.CancellationToken));
        }
        else
        {
            await Assert.ThrowsAsync<IOException>(() =>
                BoundedResponseReader.ReadAsync(content, 32, TestContext.Current.CancellationToken));
        }

        Assert.True(stream.Disposed);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, false)]
    [InlineData(HttpStatusCode.BadGateway, false)]
    [InlineData(HttpStatusCode.OK, true)]
    [InlineData(HttpStatusCode.BadGateway, true)]
    public async Task Discovery_RejectsBeforeDecoding_AndRetainsSnapshot(HttpStatusCode status, bool previous)
    {
        using var stream = new GeneratedBodyStream(long.MaxValue);
        using StubUpstreamHandler handler = new StubUpstreamHandler().MapFactory("https://example.com/index.json",
            () => new HttpResponseMessage(status) { Content = new GeneratedBodyContent(stream) });
        IOptions<MirrorOptions> options = Options.Create(new MirrorOptions
        {
            Upstream = { IndexUrl = "https://example.com/index.json", MaxServiceIndexBodyBytes = 32, DiscoveryCacheTtl = TimeSpan.Zero },
        });
        using var metrics = new MirrorMetrics();
        var client = new UpstreamClient(new TestHttpClientFactory(handler), options, NullLogger<UpstreamClient>.Instance, metrics);
        using var discovery = new DiscoveryCache(client, options, NullLogger<DiscoveryCache>.Instance, metrics, TimeProvider.System);
        if (previous)
        {
            DiscoveryCacheSeeder.SeedSnapshot(discovery, []);
            DiscoverySnapshot snapshot = discovery.CurrentSnapshot;
            Assert.Same(snapshot, await discovery.GetAsync(TestContext.Current.CancellationToken));
        }
        else
        {
            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => discovery.GetAsync(TestContext.Current.CancellationToken));
            Assert.Contains("exceeds maximum", error.Message, StringComparison.Ordinal);
        }

        Assert.Equal(33, stream.BytesRead);
        Assert.True(stream.Disposed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Limits_MustBePositive(long limit)
    {
        var options = new MirrorOptions { Upstream = { MaxServiceIndexBodyBytes = limit, MaxRewriteBodyBytes = limit } };
        ValidateOptionsResult result = new MirrorOptionsValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("MaxServiceIndexBodyBytes", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains("MaxRewriteBodyBytes", StringComparison.Ordinal));
    }
}
