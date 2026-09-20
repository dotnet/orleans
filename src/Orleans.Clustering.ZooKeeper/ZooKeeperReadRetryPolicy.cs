using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using org.apache.zookeeper;
using Polly;
using Polly.Retry;

namespace Orleans.Runtime.Membership;

internal static partial class ZooKeeperReadRetryPolicy
{
    internal const int MaxRetryAttempts = 4;
    internal static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(250);

    internal static ResiliencePipeline CreatePipeline(ILogger logger, TimeProvider timeProvider) =>
        new ResiliencePipelineBuilder { TimeProvider = timeProvider }
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = MaxRetryAttempts,
                BackoffType = DelayBackoffType.Exponential,
                Delay = RetryDelay,
                ShouldHandle = new PredicateBuilder().Handle<KeeperException.ConnectionLossException>(),
                OnRetry = args =>
                {
                    LogWarningRetryRead(logger, args.Outcome.Exception!, args.Context.OperationKey!,
                        args.AttemptNumber + 1, MaxRetryAttempts, args.RetryDelay.TotalMilliseconds);
                    return default;
                }
            })
            .Build();

    internal static ZooKeeperBasedMembershipTable.NativeOperations Wrap(
        ZooKeeperBasedMembershipTable.NativeOperations native,
        ResiliencePipeline pipeline,
        CancellationToken cancellationToken) =>
        new(
            path => ExecuteAsync("GetData", () => native.GetData(path), pipeline, cancellationToken),
            path => ExecuteAsync("GetChildren", () => native.GetChildren(path), pipeline, cancellationToken),
            path => ExecuteAsync("Sync", async () =>
            {
                await native.Sync(path);
                return true;
            }, pipeline, cancellationToken),
            native.Multi,
            native.SetData);

    private static async Task<T> ExecuteAsync<T>(
        string operationName,
        Func<Task<T>> operation,
        ResiliencePipeline pipeline,
        CancellationToken cancellationToken)
    {
        var context = ResilienceContextPool.Shared.Get(operationName, cancellationToken);
        try
        {
            return await pipeline.ExecuteAsync(async context =>
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                // Await actual native completion: cancellation ends admission, not an in-flight request.
                return await operation();
            }, context);
        }
        finally
        {
            ResilienceContextPool.Shared.Return(context);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "ZooKeeper {Operation} lost its connection. Retrying in {DelayMilliseconds}ms ({Retry}/{MaxRetries}).")]
    private static partial void LogWarningRetryRead(
        ILogger logger, Exception exception, string operation, int retry, int maxRetries, double delayMilliseconds);
}
