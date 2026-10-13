using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.DurableJobs;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;

namespace Orleans.Messaging;

/// <summary>
/// Holds the silo-wide outbox delivery pipeline used when <see cref="Configuration.DurableMessagingOptions.DeliveryResiliencePipeline"/>
/// is <see langword="null"/>.
/// </summary>
/// <remarks>
/// Orleans fails each receiver call after the response timeout, so the pipeline bounds retries and the whole attempt
/// in multiples of that timeout. Its circuit breaker is shared by every outbox in the silo, so it opens on failures
/// correlated across receivers, such as a storage outage. Delays use the durable jobs clock, which also times the
/// outbox's durable retries.
/// </remarks>
internal sealed class DefaultDeliveryResiliencePipeline(
    IOptions<SiloMessagingOptions> messagingOptions,
    [FromKeyedServices(DurableJobTimeProviderNames.DurableJobs)] TimeProvider timeProvider)
{
    private const int MaxRetryAttempts = 5;

    public ResiliencePipeline Pipeline { get; } = Create(messagingOptions.Value.ResponseTimeout, timeProvider);

    private static ResiliencePipeline Create(TimeSpan responseTimeout, TimeProvider timeProvider)
    {
        var maxTimeout = TimeSpan.FromDays(1);
        var responseTimeoutOrMax = responseTimeout > TimeSpan.Zero && responseTimeout < maxTimeout ? responseTimeout : maxTimeout;
        var shouldHandle = new PredicateBuilder().Handle<Exception>(static exception => exception is not (OperationCanceledException or BrokenCircuitException));
        return new ResiliencePipelineBuilder { TimeProvider = timeProvider }
            .AddTimeout(Clamp(responseTimeoutOrMax * (MaxRetryAttempts + 1), TimeSpan.FromSeconds(1), maxTimeout))
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = shouldHandle,
                MaxRetryAttempts = MaxRetryAttempts,
                Delay = TimeSpan.FromSeconds(1),
                MaxDelay = responseTimeoutOrMax,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
            })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                ShouldHandle = shouldHandle,
                FailureRatio = 0.5,
                MinimumThroughput = 20,
                SamplingDuration = TimeSpan.FromSeconds(30),
                BreakDuration = Clamp(responseTimeoutOrMax, TimeSpan.FromSeconds(1), maxTimeout),
            })
            .Build();
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max) => value < min ? min : value > max ? max : value;
}
