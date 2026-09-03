using System.Net;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Polly;
using Polly.Retry;

namespace Orleans.Reminders.Cosmos;

internal static partial class CosmosReadRetryPolicy
{
    internal const string PipelineKey = "Orleans.Reminders.Cosmos.Read";
    internal const int MaxRetryAttempts = 2;
    private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromMilliseconds(100);

    internal static IServiceCollection AddCosmosReadRetryPolicy(this IServiceCollection services)
    {
        services.AddResiliencePipeline(PipelineKey, static (builder, context) =>
        {
            builder.TimeProvider = context.ServiceProvider.GetRequiredService<TimeProvider>();
            Configure(
                builder,
                context.ServiceProvider.GetRequiredService<ILogger<CosmosReminderTable>>(),
                DefaultRetryDelay);
        });

        return services;
    }

    internal static void Configure(
        ResiliencePipelineBuilder builder,
        ILogger<CosmosReminderTable> logger,
        TimeSpan retryDelay) =>
        builder.AddRetry(new RetryStrategyOptions
        {
            MaxRetryAttempts = MaxRetryAttempts,
            BackoffType = DelayBackoffType.Linear,
            Delay = retryDelay,
            ShouldHandle = new PredicateBuilder().Handle<CosmosException>(
                static exception => exception.StatusCode == HttpStatusCode.RequestTimeout),
            OnRetry = args =>
            {
                var exception = (CosmosException)args.Outcome.Exception!;
                LogWarningReadTimedOut(
                    logger,
                    exception,
                    args.AttemptNumber + 1,
                    MaxRetryAttempts,
                    args.RetryDelay.TotalMilliseconds,
                    exception.ActivityId);
                return default;
            }
        });

    internal static ResiliencePipeline CreatePipeline(
        ILogger<CosmosReminderTable> logger,
        TimeProvider timeProvider) =>
        CreatePipeline(logger, timeProvider, DefaultRetryDelay);

    internal static ResiliencePipeline CreatePipeline(
        ILogger<CosmosReminderTable> logger,
        TimeProvider timeProvider,
        TimeSpan retryDelay)
    {
        var builder = new ResiliencePipelineBuilder { TimeProvider = timeProvider };
        Configure(builder, logger, retryDelay);
        return builder.Build();
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Cosmos DB reminder read timed out. Retrying in {DelayMilliseconds}ms ({Retry}/{MaxRetries}). ActivityId: {ActivityId}"
    )]
    private static partial void LogWarningReadTimedOut(
        ILogger logger,
        Exception exception,
        int retry,
        int maxRetries,
        double delayMilliseconds,
        string activityId);
}
