using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

using NuGetMirror.Configuration;

using Polly;
using Polly.CircuitBreaker;

namespace NuGetMirror.Upstream;

internal static class UpstreamResilienceExtensions
{
    private const string BufferedHandlerName = "upstream-buffered";
    private const string StreamHandlerName = "upstream-stream";

    public static IServiceCollection AddUpstreamResilience(this IServiceCollection services)
    {
        services.AddSingleton(sp =>
        {
            UpstreamResilienceOptions opts = sp.GetRequiredService<IOptions<MirrorOptions>>().Value.Upstream.Resilience;
            return new ResiliencePipelineBuilder<HttpResponseMessage>()
                .AddCircuitBreaker(new CircuitBreakerStrategyOptions<HttpResponseMessage>
                {
                    FailureRatio = opts.FailureRatio,
                    SamplingDuration = opts.SamplingDuration,
                    MinimumThroughput = opts.MinimumThroughput,
                    BreakDuration = opts.BreakDuration,
                    ShouldHandle = UpstreamResiliencePredicates.CreateTransientPredicate(),
                })
                .Build();
        });

        services.AddHttpClient(UpstreamClient.BufferedClientName)
            .ConfigurePrimaryHttpMessageHandler(ConfigureHandler)
            .AddResilienceHandler(BufferedHandlerName, static (builder, context) =>
            {
                UpstreamResilienceOptions opts = context.ServiceProvider.GetRequiredService<IOptions<MirrorOptions>>().Value.Upstream.Resilience;

                if (!opts.Enabled)
                {
                    return;
                }

                ResiliencePipeline<HttpResponseMessage> sharedBreaker = context.ServiceProvider.GetRequiredService<ResiliencePipeline<HttpResponseMessage>>();

                builder.AddTimeout(opts.TotalRequestTimeout);

                builder.AddRetry(new HttpRetryStrategyOptions
                {
                    MaxRetryAttempts = opts.MaxRetryAttempts,
                    Delay = opts.BaseDelay,
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = true,
                    ShouldHandle = UpstreamResiliencePredicates.CreateTransientPredicate(),
                });

                builder.AddPipeline(sharedBreaker);

                builder.AddTimeout(opts.AttemptTimeout);
            });

        services.AddHttpClient(UpstreamClient.StreamClientName)
            .ConfigurePrimaryHttpMessageHandler(ConfigureHandler)
            .AddResilienceHandler(StreamHandlerName, static (builder, context) =>
            {
                UpstreamResilienceOptions opts = context.ServiceProvider.GetRequiredService<IOptions<MirrorOptions>>().Value.Upstream.Resilience;

                if (!opts.Enabled)
                {
                    return;
                }

                ResiliencePipeline<HttpResponseMessage> sharedBreaker = context.ServiceProvider.GetRequiredService<ResiliencePipeline<HttpResponseMessage>>();

                builder.AddTimeout(opts.HeadersTimeout);

                builder.AddRetry(new HttpRetryStrategyOptions
                {
                    MaxRetryAttempts = opts.MaxRetryAttempts,
                    Delay = opts.BaseDelay,
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = true,
                    ShouldHandle = UpstreamResiliencePredicates.CreateTransientPredicate(),
                });

                builder.AddPipeline(sharedBreaker);
            });

        services.AddSingleton<UpstreamClient>();

        return services;
    }

    private static SocketsHttpHandler ConfigureHandler(IServiceProvider sp)
    {
        UpstreamOptions opts = sp.GetRequiredService<IOptions<MirrorOptions>>().Value.Upstream;
        return UpstreamHandlerFactory.CreateHandler(opts);
    }
}
