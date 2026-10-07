using System.Net;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

using NuGetMirror.Configuration;
using NuGetMirror.Upstream;

using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;

namespace NuGetMirror.UnitTests;

public sealed class UpstreamResilienceTests
{
    [Fact]
    public void ValidateOptions_FailsOnInvalidFailureRatio()
    {
        var services = new ServiceCollection();
        services.AddOptions<MirrorOptions>()
            .Configure(o => o.Upstream.Resilience.FailureRatio = 2.0);
        services.AddSingleton<IValidateOptions<MirrorOptions>, MirrorOptionsValidator>();
        using ServiceProvider sp = services.BuildServiceProvider();

        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(
            () => sp.GetRequiredService<IOptions<MirrorOptions>>().Value);

        Assert.Contains("FailureRatio", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateOptions_FailsOnInvalidMaxRetryAttempts()
    {
        var services = new ServiceCollection();
        services.AddOptions<MirrorOptions>()
            .Configure(o => o.Upstream.Resilience.MaxRetryAttempts = 20);
        services.AddSingleton<IValidateOptions<MirrorOptions>, MirrorOptionsValidator>();
        using ServiceProvider sp = services.BuildServiceProvider();

        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(
            () => sp.GetRequiredService<IOptions<MirrorOptions>>().Value);

        Assert.Contains("MaxRetryAttempts", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateOptions_FailsOnInvalidMinimumThroughput()
    {
        var services = new ServiceCollection();
        services.AddOptions<MirrorOptions>()
            .Configure(o => o.Upstream.Resilience.MinimumThroughput = 1);
        services.AddSingleton<IValidateOptions<MirrorOptions>, MirrorOptionsValidator>();
        using ServiceProvider sp = services.BuildServiceProvider();

        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(
            () => sp.GetRequiredService<IOptions<MirrorOptions>>().Value);

        Assert.Contains("MinimumThroughput", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateOptions_PassesOnValidConfig()
    {
        var services = new ServiceCollection();
        services.AddOptions<MirrorOptions>()
            .Configure(o =>
            {
                o.Upstream.Resilience.FailureRatio = 0.1;
                o.Upstream.Resilience.MaxRetryAttempts = 3;
                o.Upstream.Resilience.MinimumThroughput = 10;
            });
        services.AddSingleton<IValidateOptions<MirrorOptions>, MirrorOptionsValidator>();
        using ServiceProvider sp = services.BuildServiceProvider();
        IValidateOptions<MirrorOptions> validator = sp.GetRequiredService<IValidateOptions<MirrorOptions>>();
        MirrorOptions options = sp.GetRequiredService<IOptions<MirrorOptions>>().Value;

        ValidateOptionsResult result = validator.Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task RetryPipeline_RetriesOnTransientAndSucceeds()
    {
        int attempts = 0;
        ResiliencePipeline<HttpResponseMessage> pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = 3,
                Delay = TimeSpan.Zero,
                ShouldHandle = UpstreamResiliencePredicates.CreateTransientPredicate(),
            })
            .Build();

        HttpResponseMessage result = await pipeline.ExecuteAsync(ct =>
        {
            int current = Interlocked.Increment(ref attempts);
            if (current <= 2)
            {
                return ValueTask.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }

            return ValueTask.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task RetryPipeline_DoesNotRetryOnNonTransient()
    {
        int attempts = 0;
        ResiliencePipeline<HttpResponseMessage> pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = 3,
                Delay = TimeSpan.Zero,
                ShouldHandle = UpstreamResiliencePredicates.CreateTransientPredicate(),
            })
            .Build();

        HttpResponseMessage result = await pipeline.ExecuteAsync(ct =>
        {
            Interlocked.Increment(ref attempts);
            return ValueTask.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task RetryPipeline_RetriesOnHttpRequestException()
    {
        int attempts = 0;
        ResiliencePipeline<HttpResponseMessage> pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = 2,
                Delay = TimeSpan.Zero,
                ShouldHandle = UpstreamResiliencePredicates.CreateTransientPredicate(),
            })
            .Build();

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            pipeline.ExecuteAsync<HttpResponseMessage>(ct =>
                {
                    Interlocked.Increment(ref attempts);
                    throw new HttpRequestException("Network error");
                }, TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task RetryPipeline_RetriesOnConnectionTimeout()
    {
        int attempts = 0;
        ResiliencePipeline<HttpResponseMessage> pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = 2,
                Delay = TimeSpan.Zero,
                ShouldHandle = UpstreamResiliencePredicates.CreateTransientPredicate(),
            })
            .Build();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            pipeline.ExecuteAsync<HttpResponseMessage>(ct =>
                {
                    Interlocked.Increment(ref attempts);
                    throw new OperationCanceledException("Connection timed out", new TimeoutException());
                }, TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task RetryPipeline_HonorsRetryAfterHeader()
    {
        int attempts = 0;
        var retryAfterDelay = TimeSpan.FromMilliseconds(200);
        ResiliencePipeline<HttpResponseMessage> pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromMilliseconds(50),
                ShouldHandle = UpstreamResiliencePredicates.CreateTransientPredicate(),
                ShouldRetryAfterHeader = true,
            })
            .Build();

        DateTimeOffset started = DateTimeOffset.UtcNow;

        HttpResponseMessage result = await pipeline.ExecuteAsync(ct =>
        {
            Interlocked.Increment(ref attempts);
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(retryAfterDelay);
            return ValueTask.FromResult(response);
        }, TestContext.Current.CancellationToken);

        TimeSpan elapsed = DateTimeOffset.UtcNow - started;

        Assert.Equal(HttpStatusCode.TooManyRequests, result.StatusCode);
        Assert.Equal(4, attempts);
        Assert.True(elapsed >= retryAfterDelay * 3,
            $"Expected total elapsed >= {retryAfterDelay * 3} but was {elapsed}");
    }

    [Fact]
    public async Task SharedCircuitBreaker_OpensAfterThreshold()
    {
        ResiliencePipeline<HttpResponseMessage> breaker = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<HttpResponseMessage>
            {
                FailureRatio = 0.5,
                SamplingDuration = TimeSpan.FromSeconds(30),
                MinimumThroughput = 4,
                BreakDuration = TimeSpan.FromMinutes(1),
                ShouldHandle = UpstreamResiliencePredicates.CreateTransientPredicate(),
            })
            .Build();

        // Execute failures until the breaker opens (2 of 4 = 0.5)
        int failures = 0;

        while (true)
        {
            try
            {
                await breaker.ExecuteAsync(ct =>
                    {
                        failures++;
                        return ValueTask.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                    }, TestContext.Current.CancellationToken);
            }
            catch (BrokenCircuitException)
            {
                break;
            }
        }

        Assert.True(failures >= 2,
            $"Breaker should have opened after at least 2 failures but had {failures}");
    }

    [Fact]
    public async Task SharedCircuitBreaker_AffectsAllConsumers()
    {
        ResiliencePipeline<HttpResponseMessage> breaker = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<HttpResponseMessage>
            {
                FailureRatio = 0.6,
                SamplingDuration = TimeSpan.FromSeconds(30),
                MinimumThroughput = 5,
                BreakDuration = TimeSpan.FromMinutes(1),
                ShouldHandle = UpstreamResiliencePredicates.CreateTransientPredicate(),
            })
            .Build();

        // Build pipeline A (buffered) that includes the shared breaker
        ResiliencePipeline<HttpResponseMessage> pipelineA = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddRetry(new RetryStrategyOptions<HttpResponseMessage>
            {
                MaxRetryAttempts = 2,
                Delay = TimeSpan.Zero,
                ShouldHandle = UpstreamResiliencePredicates.CreateTransientPredicate(),
            })
            .AddPipeline(breaker)
            .Build();

        // Build pipeline B (streaming) that includes the same shared breaker
        ResiliencePipeline<HttpResponseMessage> pipelineB = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddPipeline(breaker)
            .Build();

        // Generate enough failures through pipeline A to open the breaker
        int aFailures = 0;

        while (true)
        {
            try
            {
                await pipelineA.ExecuteAsync(ct =>
                    {
                        aFailures++;
                        return ValueTask.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                    }, TestContext.Current.CancellationToken);
            }
            catch (BrokenCircuitException)
            {
                break;
            }
            catch (HttpRequestException)
            {
                // Retry exhausted — breaker didn't open yet. This can happen if retry
                // consumes attempts before the breaker sees enough failures. Keep going.
            }
        }

        // Now pipeline B should immediately reject with BrokenCircuitException
        BrokenCircuitException bRejected = await Assert.ThrowsAsync<BrokenCircuitException>(() =>
            pipelineB.ExecuteAsync(ct =>
                ValueTask.FromResult(new HttpResponseMessage(HttpStatusCode.OK)),
                TestContext.Current.CancellationToken).AsTask());
    }
}
