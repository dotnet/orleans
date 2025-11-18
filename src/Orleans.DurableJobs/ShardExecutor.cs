using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Hosting;
using Orleans.Runtime;

namespace Orleans.DurableJobs;

/// <summary>
/// Handles the execution of job shards and individual durable jobs.
/// </summary>
internal sealed partial class ShardExecutor
{
    private readonly IInternalGrainFactory _grainFactory;
    private readonly ILogger<ShardExecutor> _logger;
    private readonly DurableJobsOptions _options;
    private readonly SemaphoreSlim _jobConcurrencyLimiter;

    /// <summary>
    /// Initializes a new instance of the <see cref="ShardExecutor"/> class.
    /// </summary>
    /// <param name="grainFactory">The grain factory for creating grain references.</param>
    /// <param name="options">The durable jobs configuration options.</param>
    /// <param name="logger">The logger instance.</param>
    public ShardExecutor(
        IInternalGrainFactory grainFactory,
        IOptions<DurableJobsOptions> options,
        ILogger<ShardExecutor> logger)
    {
        _grainFactory = grainFactory;
        _logger = logger;
        _options = options.Value;
        _jobConcurrencyLimiter = new SemaphoreSlim(_options.MaxConcurrentJobsPerSilo);
    }

    /// <summary>
    /// Runs a shard, processing all jobs within it until completion or cancellation.
    /// </summary>
    /// <param name="shard">The shard to execute.</param>
    /// <param name="cancellationToken">Cancellation token to stop processing.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task RunShardAsync(IJobShard shard, CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding | ConfigureAwaitOptions.ContinueOnCapturedContext);

        var tasks = new ConcurrentDictionary<string, Task>();
        try
        {
            if (shard.StartTime > DateTime.UtcNow)
            {
                // Wait until the shard's start time
                var delay = shard.StartTime - DateTimeOffset.UtcNow;
                LogWaitingForShardStartTime(_logger, shard.Id, delay, shard.StartTime);
                await Task.Delay(delay, cancellationToken);
            }

            LogBeginProcessingShard(_logger, shard.Id);

            // Process all jobs in the shard
            await foreach (var jobContext in shard.ConsumeDurableJobsAsync().WithCancellation(cancellationToken))
            {
                // Wait for concurrency slot
                await _jobConcurrencyLimiter.WaitAsync(cancellationToken);
                // Start processing the job. RunJobAsync will release the semaphore when done and remove itself from the tasks dictionary
                tasks[jobContext.Job.Id] = RunJobAsync(jobContext, shard, tasks, cancellationToken);
            }

            LogCompletedProcessingShard(_logger, shard.Id);
        }
        catch (OperationCanceledException)
        {
            LogShardCancelled(_logger, shard.Id);
            throw;
        }
        finally
        {
            // Wait for all jobs to complete
            await Task.WhenAll(tasks.Values);
        }
    }

    private async Task RunJobAsync(
        IDurableJobContext jobContext,
        IJobShard shard,
        ConcurrentDictionary<string, Task> runningTasks,
        CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.ForceYielding);

        try
        {
            LogExecutingJob(_logger, jobContext.Job.Id, jobContext.Job.Name, jobContext.Job.TargetGrainId, jobContext.Job.DueTime);

            var target = _grainFactory
                .GetGrain(jobContext.Job.TargetGrainId)
                .AsReference<IDurableJobReceiverExtension>();

            await target.DeliverDurableJobAsync(jobContext, cancellationToken);
            await shard.RemoveJobAsync(jobContext.Job.Id, cancellationToken);

            LogJobExecutedSuccessfully(_logger, jobContext.Job.Id, jobContext.Job.Name);
        }
        catch (Exception ex) when (ex is not TaskCanceledException)
        {
            LogErrorExecutingJob(_logger, ex, jobContext.Job.Id);
            var retryTime = _options.ShouldRetry(jobContext, ex);
            if (retryTime is not null)
            {
                LogRetryingJob(_logger, jobContext.Job.Id, jobContext.Job.Name, retryTime.Value, jobContext.DequeueCount);
                await shard.RetryJobLaterAsync(jobContext, retryTime.Value, cancellationToken);
            }
            else
            {
                LogJobFailedNoRetry(_logger, jobContext.Job.Id, jobContext.Job.Name, jobContext.DequeueCount);
            }
        }
        finally
        {
            _jobConcurrencyLimiter.Release();
            runningTasks.TryRemove(jobContext.Job.Id, out _);
        }
    }
}
