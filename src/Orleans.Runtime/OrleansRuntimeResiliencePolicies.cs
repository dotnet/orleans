using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime.Placement;
using Polly;
using Polly.Retry;
using Polly.Timeout;

namespace Orleans.Runtime;

/// <summary>
/// Resilience policies used by the Orleans runtime.
/// </summary>
public static class OrleansRuntimeResiliencePolicies
{
    /// <summary>
    /// The key used to identify the placement resilience pipeline in <see cref="Polly.Registry.ResiliencePipelineProvider{TKey}"/>.
    /// </summary>
    public const string PlacementResiliencePipelineKey = "Orleans.Placement";

    /// <summary>
    /// Adds all Orleans runtime resilience policies to the service collection.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    internal static IServiceCollection AddOrleansRuntimeResiliencePolicies(IServiceCollection services)
    {
        // Placement resilience pipeline
        services.AddResiliencePipeline(PlacementResiliencePipelineKey, static (builder, context) =>
        {
            var options = context.ServiceProvider.GetRequiredService<IOptions<SiloMessagingOptions>>().Value;
            var logger = context.ServiceProvider.GetRequiredService<ILogger<PlacementService>>();

            builder
                .AddTimeout(new TimeoutStrategyOptions
                {
                    Timeout = options.PlacementTimeout,
                    OnTimeout = args =>
                    {
                        logger.LogWarning("Grain placement operation timed out after {Timeout}.", options.PlacementTimeout);
                        return default;
                    }
                })
                .AddRetry(new RetryStrategyOptions
                {
                    MaxRetryAttempts = options.PlacementMaxRetries,
                    BackoffType = DelayBackoffType.Exponential,
                    Delay = options.PlacementRetryBaseDelay,
                    UseJitter = true,
                    ShouldHandle = new PredicateBuilder().Handle<Exception>(ex => IsTransientPlacementException(ex)),
                    OnRetry = args =>
                    {
                        logger.LogDebug(args.Outcome.Exception, "Retrying grain placement operation. Attempt: {AttemptNumber}, Delay: {RetryDelay}.", args.AttemptNumber, args.RetryDelay);
                        return default;
                    }
                });
        });

        return services;
    }

    /// <summary>
    /// Determines whether an exception is transient and should be retried during placement operations.
    /// </summary>
    private static bool IsTransientPlacementException(Exception exception) =>
        exception switch
        {
            OrleansException => true,
            TimeoutException => true,
            OperationCanceledException => false,
            _ => false
        };
}
