using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Diagnostics;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Runtime.Messaging;
namespace Orleans.DurableJobs;

/// <summary>
/// Handles the execution of job shards and individual durable jobs.
/// </summary>
internal sealed partial class ShardExecutor
{
    private readonly IInternalGrainFactory _grainFactory;
    private readonly ILogger<ShardExecutor> _logger;
    private readonly DurableJobsOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly DurableJobsInstruments _durableJobsInstruments;
    private readonly SemaphoreSlim _jobConcurrencyLimiter;
    private readonly IOverloadDetector _overloadDetector;
    private int _currentCapacity;
    private int _slowStartRampUpStarted;

    /// <summary>
    /// Initializes a new instance of the <see cref="ShardExecutor"/> class.
    /// </summary>
    /// <param name="grainFactory">The grain factory for creating grain references.</param>
    /// <param name="options">The durable jobs configuration options.</param>
    /// <param name="overloadDetector">The overload detector for throttling job execution.</param>
    /// <param name="logger">The logger instance.</param>
    public ShardExecutor(
        IInternalGrainFactory grainFactory,
        IOptions<DurableJobsOptions> options,
        IOverloadDetector overloadDetector,
        ILogger<ShardExecutor> logger,
        [FromKeyedServices(DurableJobTimeProviderNames.DurableJobs)] TimeProvider? timeProvider = null,
        DurableJobsInstruments? durableJobsInstruments = null)
    {
        _grainFactory = grainFactory;
        _logger = logger;
        _options = options.Value;
        _overloadDetector = overloadDetector;
        _durableJobsInstruments = durableJobsInstruments ?? DurableJobsInstruments.CreateForDirectConstruction();
        _timeProvider = timeProvider ?? TimeProvider.System;

        _currentCapacity = _options.ConcurrencySlowStartEnabled && _options.SlowStartInitialConcurrency < _options.MaxConcurrentJobsPerSilo
            ? _options.SlowStartInitialConcurrency
            : _options.MaxConcurrentJobsPerSilo;

        _jobConcurrencyLimiter = new SemaphoreSlim(_currentCapacity);
    }

    /// <summary>
    /// Runs a shard, processing all jobs within it until completion or attempt cancellation.
    /// </summary>
    /// <param name="shard">The shard to execute.</param>
    /// <param name="attemptCancellationToken">
    /// A token which cooperatively requests cancellation of the current shard and job execution attempts.
    /// Durable jobs remain eligible for processing by another owner.
    /// </param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task RunShardAsync(IJobShard shard, CancellationToken attemptCancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding | ConfigureAwaitOptions.ContinueOnCapturedContext);
        using var shardAttemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(attemptCancellationToken);
        var shardAttemptCancellationToken = shardAttemptCancellation.Token;
        var ownershipLost = 0;

        void NotifyOwnershipLost()
        {
            if (Interlocked.Exchange(ref ownershipLost, 1) == 0)
            {
                shardAttemptCancellation.Cancel();
            }
        }

        EnsureSlowStartRampUpStarted();

        var tasks = new ConcurrentDictionary<string, Task>();
        var taskFailures = new ConcurrentQueue<ExceptionDispatchInfo>();
        var processingStarted = false;
        var processingStartTimestamp = 0L;
        var attemptCanceled = false;
        var error = false;
        try
        {
            var now = _timeProvider.GetUtcNow();
            if (shard.StartTime > now)
            {
                // Wait until the shard's start time
                var delay = shard.StartTime - now;
                LogWaitingForShardStartTime(_logger, shard.Id, delay, shard.StartTime);
                await Task.Delay(delay, _timeProvider, shardAttemptCancellationToken);
            }

            LogBeginProcessingShard(_logger, shard.Id);
            processingStarted = true;
            processingStartTimestamp = _timeProvider.GetTimestamp();

            // Process all jobs in the shard
            await foreach (var jobContext in shard.ConsumeDurableJobsAsync().WithCancellation(shardAttemptCancellationToken))
            {
                await WaitForOverloadToClearAsync(shard.Id, shardAttemptCancellationToken);

                // Wait for concurrency slot
                await _jobConcurrencyLimiter.WaitAsync(shardAttemptCancellationToken);

                var attemptStarted = false;
                try
                {
                    var startResult = await shard.TryStartAttemptAsync(jobContext, shardAttemptCancellationToken);
                    if (startResult == DurableJobMutationResult.JobNotFound)
                    {
                        continue;
                    }

                    if (startResult == DurableJobMutationResult.OwnershipLost)
                    {
                        NotifyOwnershipLost();
                        shardAttemptCancellationToken.ThrowIfCancellationRequested();
                    }

                    if (startResult != DurableJobMutationResult.Applied)
                    {
                        throw new InvalidOperationException($"Unsupported durable job mutation result: {startResult}.");
                    }

                    // ExecuteJobAsync owns the concurrency slot after this point.
                    tasks[jobContext.RunId] = ExecuteJobAsync(
                        jobContext,
                        shard,
                        tasks,
                        taskFailures,
                        shardAttemptCancellationToken,
                        NotifyOwnershipLost);
                    attemptStarted = true;
                }
                finally
                {
                    if (!attemptStarted)
                    {
                        _jobConcurrencyLimiter.Release();
                    }
                }
            }

            LogCompletedProcessingShard(_logger, shard.Id);
        }
        catch (OperationCanceledException) when (shardAttemptCancellationToken.IsCancellationRequested)
        {
            attemptCanceled = true;
            if (Volatile.Read(ref ownershipLost) != 0)
            {
                LogShardOwnershipLost(_logger, shard.Id);
            }
            else
            {
                LogShardAttemptCanceled(_logger, shard.Id);
                throw;
            }
        }
        catch
        {
            error = true;
            throw;
        }
        finally
        {
            // Wait for all jobs to complete
            try
            {
                await Task.WhenAll(tasks.Values);
                if (taskFailures.TryDequeue(out var failure))
                {
                    failure.Throw();
                }
            }
            catch (OperationCanceledException) when (shardAttemptCancellationToken.IsCancellationRequested)
            {
                if (!attemptCanceled)
                {
                    attemptCanceled = true;
                    if (Volatile.Read(ref ownershipLost) != 0)
                    {
                        LogShardOwnershipLost(_logger, shard.Id);
                    }
                    else
                    {
                        LogShardAttemptCanceled(_logger, shard.Id);
                    }
                }

                if (Volatile.Read(ref ownershipLost) == 0)
                {
                    throw;
                }
            }
            catch
            {
                error = true;
                throw;
            }
            finally
            {
                if (processingStarted)
                {
                    if (!attemptCanceled && Volatile.Read(ref ownershipLost) != 0)
                    {
                        attemptCanceled = true;
                        LogShardOwnershipLost(_logger, shard.Id);
                    }

                    _durableJobsInstruments.OnShardProcessed(
                        _timeProvider.GetElapsedTime(processingStartTimestamp),
                        attemptCanceled,
                        error);
                }
            }
        }
    }

    private void EnsureSlowStartRampUpStarted()
    {
        if (Volatile.Read(ref _currentCapacity) < _options.MaxConcurrentJobsPerSilo
            && Interlocked.CompareExchange(ref _slowStartRampUpStarted, 1, 0) == 0)
        {
            _ = Task.Run(SlowStartRampUpAsync);
        }
    }

    private async Task WaitForOverloadToClearAsync(string shardId, CancellationToken cancellationToken)
    {
        // Check for overload and pause batch processing if needed
        if (_overloadDetector.IsOverloaded)
        {
            LogOverloadDetected(_logger, shardId);
            while (_overloadDetector.IsOverloaded)
            {
                await Task.Delay(_options.OverloadBackoffDelay, _timeProvider, cancellationToken);
            }

            LogOverloadCleared(_logger, shardId);
        }
    }

    private async Task SlowStartRampUpAsync()
    {
        var targetCapacity = _options.MaxConcurrentJobsPerSilo;
        LogSlowStartBegin(_logger, Volatile.Read(ref _currentCapacity), targetCapacity, _options.SlowStartInterval);

        try
        {
            while (Volatile.Read(ref _currentCapacity) < targetCapacity)
            {
                await Task.Delay(_options.SlowStartInterval, _timeProvider, CancellationToken.None);

                while (true)
                {
                    var currentCapacity = Volatile.Read(ref _currentCapacity);
                    if (currentCapacity >= targetCapacity)
                    {
                        break;
                    }

                    var newCapacity = (int)Math.Min((long)currentCapacity * 2, targetCapacity);
                    var toRelease = newCapacity - currentCapacity;
                    if (toRelease <= 0)
                    {
                        break;
                    }

                    if (Interlocked.CompareExchange(ref _currentCapacity, newCapacity, currentCapacity) == currentCapacity)
                    {
                        _jobConcurrencyLimiter.Release(toRelease);
                        LogSlowStartConcurrencyIncreased(_logger, newCapacity, targetCapacity);
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // If the ramp-up fails for any reason, release all remaining capacity to avoid being stuck at low concurrency.
            var currentCapacity = Volatile.Read(ref _currentCapacity);
            var remaining = targetCapacity - currentCapacity;
            if (remaining > 0)
            {
                _jobConcurrencyLimiter.Release(remaining);
                Interlocked.Exchange(ref _currentCapacity, targetCapacity);
            }

            LogSlowStartError(_logger, ex);
            return;
        }

        LogSlowStartComplete(_logger, Volatile.Read(ref _currentCapacity));
    }

    private async Task ExecuteJobAsync(
        IJobRunContext jobContext,
        IJobShard shard,
        ConcurrentDictionary<string, Task>? runningTasks,
        ConcurrentQueue<ExceptionDispatchInfo> taskFailures,
        CancellationToken attemptCancellationToken,
        Action notifyOwnershipLost)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.ForceYielding);

        Exception? failureException = null;
        var attemptStartTimestamp = _timeProvider.GetTimestamp();
        _durableJobsInstruments.OnJobAttemptStarted(_timeProvider.GetUtcNow() - jobContext.Job.DueTime);

        using var activity = DurableJobsDiagnostics.StartExecuteActivity(jobContext.Job, jobContext.DequeueCount, jobContext.RunId);
        try
        {
            try
            {
                attemptCancellationToken.ThrowIfCancellationRequested();
                LogExecutingJob(_logger, jobContext.Job.Id, jobContext.Job.Name, jobContext.Job.TargetGrainId, jobContext.Job.DueTime);

                var target = _grainFactory.GetGrain<IDurableJobReceiverExtension>(jobContext.Job.TargetGrainId);

                var result = await target.HandleDurableJobAsync(jobContext, attemptCancellationToken);

                // Handle the result based on status
                while (result.IsInProgress)
                {
                    // Enter polling loop
                    LogPollingJob(_logger, jobContext.Job.Id, jobContext.Job.Name, result.PollAfterDelay.Value);

                    await Task.Delay(result.PollAfterDelay.Value, _timeProvider, attemptCancellationToken);

                    result = await target.HandleDurableJobAsync(jobContext, attemptCancellationToken);
                }

                switch (result.Status)
                {
                    case DurableJobRunStatus.Completed:
                        var removeResult = await shard.RemoveJobAsync(jobContext.Job.Id, attemptCancellationToken);
                        if (removeResult == DurableJobMutationResult.Applied)
                        {
                            LogJobExecutedSuccessfully(_logger, jobContext.Job.Id, jobContext.Job.Name);
                            _durableJobsInstruments.OnJobCompleted(_timeProvider.GetElapsedTime(attemptStartTimestamp));
                            activity?.SetTag(ActivityTagKeys.DurableJobStatus, "completed");
                            activity?.SetStatus(ActivityStatusCode.Ok);
                        }
                        else if (removeResult == DurableJobMutationResult.JobNotFound)
                        {
                            LogJobMutationNotApplied(
                                _logger,
                                jobContext.Job.Id,
                                jobContext.Job.Name,
                                "completion",
                                removeResult);
                            activity?.SetTag(ActivityTagKeys.DurableJobStatus, "cancellation_requested");
                        }
                        else
                        {
                            LogJobMutationNotApplied(
                                _logger,
                                jobContext.Job.Id,
                                jobContext.Job.Name,
                                "completion",
                                removeResult);
                            activity?.SetTag(ActivityTagKeys.DurableJobStatus, "ownership_lost");
                            notifyOwnershipLost();
                        }
                        break;
                    case DurableJobRunStatus.RescheduleRequested when result.RescheduleTime is { } rescheduleTime:
                        var rescheduleResult = await shard.RescheduleJobAsync(jobContext, rescheduleTime, attemptCancellationToken);
                        if (rescheduleResult == DurableJobMutationResult.Applied)
                        {
                            LogReschedulingJob(_logger, jobContext.Job.Id, jobContext.Job.Name, rescheduleTime);
                            _durableJobsInstruments.OnJobRescheduled(_timeProvider.GetElapsedTime(attemptStartTimestamp));
                            activity?.SetTag(ActivityTagKeys.DurableJobStatus, "rescheduled");
                            activity?.SetStatus(ActivityStatusCode.Ok);
                        }
                        else
                        {
                            LogJobMutationNotApplied(
                                _logger,
                                jobContext.Job.Id,
                                jobContext.Job.Name,
                                "reschedule",
                                rescheduleResult);
                            activity?.SetTag(
                                ActivityTagKeys.DurableJobStatus,
                                rescheduleResult == DurableJobMutationResult.JobNotFound
                                    ? "cancellation_requested"
                                    : "ownership_lost");
                            if (rescheduleResult == DurableJobMutationResult.OwnershipLost)
                            {
                                notifyOwnershipLost();
                            }
                        }
                        break;
                    case DurableJobRunStatus.RescheduleRequested:
                        failureException = new InvalidOperationException(
                            $"Durable job '{jobContext.Job.Id}' returned RescheduleRequested without a reschedule time.");
                        LogErrorExecutingJob(_logger, failureException, jobContext.Job.Id);
                        break;
                    case DurableJobRunStatus.Failed when result.Exception is { } exception:
                        LogJobFailedWithResult(_logger, jobContext.Job.Id, jobContext.Job.Name);
                        failureException = exception;
                        break;
                    case DurableJobRunStatus.Failed:
                        failureException = new InvalidOperationException(
                            $"Durable job '{jobContext.Job.Id}' returned Failed without an exception.");
                        LogErrorExecutingJob(_logger, failureException, jobContext.Job.Id);
                        break;
                    default:
                        failureException = new InvalidOperationException(
                            $"Durable job '{jobContext.Job.Id}' returned unsupported status value {(int)result.Status}.");
                        LogErrorExecutingJob(_logger, failureException, jobContext.Job.Id);
                        break;
                }
            }
            catch (OperationCanceledException) when (attemptCancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogErrorExecutingJob(_logger, ex, jobContext.Job.Id);
                failureException = ex;
            }

            // Cancellation is handled by shard takeover, so only non-cancellation failures are retried here.
            if (failureException is not null)
            {
                var retryTime = _options.ShouldRetry(jobContext, failureException);
                if (retryTime is not null)
                {
                    var retryResult = await shard.RetryJobLaterAsync(jobContext, retryTime.Value, attemptCancellationToken);
                    if (retryResult == DurableJobMutationResult.Applied)
                    {
                        LogRetryingJob(_logger, jobContext.Job.Id, jobContext.Job.Name, retryTime.Value, jobContext.DequeueCount);
                        _durableJobsInstruments.OnJobRetried(_timeProvider.GetElapsedTime(attemptStartTimestamp));
                        activity?.SetTag(ActivityTagKeys.DurableJobStatus, "retried");
                        DurableJobsDiagnostics.SetError(activity, failureException);
                    }
                    else
                    {
                        LogJobMutationNotApplied(
                            _logger,
                            jobContext.Job.Id,
                            jobContext.Job.Name,
                            "retry",
                            retryResult);
                        activity?.SetTag(
                            ActivityTagKeys.DurableJobStatus,
                            retryResult == DurableJobMutationResult.JobNotFound
                                ? "cancellation_requested"
                                : "ownership_lost");
                        if (retryResult == DurableJobMutationResult.OwnershipLost)
                        {
                            notifyOwnershipLost();
                        }
                    }
                }
                else
                {
                    LogJobFailedNoRetry(_logger, jobContext.Job.Id, jobContext.Job.Name, jobContext.DequeueCount);
                    _durableJobsInstruments.OnJobFailed(_timeProvider.GetElapsedTime(attemptStartTimestamp));
                    activity?.SetTag(ActivityTagKeys.DurableJobStatus, "failed");
                    DurableJobsDiagnostics.SetError(activity, failureException);
                }
            }

        }
        catch (OperationCanceledException exception) when (attemptCancellationToken.IsCancellationRequested)
        {
            activity?.SetTag(ActivityTagKeys.DurableJobStatus, "attempt_canceled");
            taskFailures.Enqueue(ExceptionDispatchInfo.Capture(exception));
        }
        catch (Exception exception)
        {
            DurableJobsDiagnostics.SetError(activity, exception);
            taskFailures.Enqueue(ExceptionDispatchInfo.Capture(exception));
        }
        finally
        {
            // Cleanup must happen even when retry persistence throws, otherwise slots leak and shard processing can stall.
            _jobConcurrencyLimiter.Release();
            runningTasks?.TryRemove(jobContext.RunId, out _);
        }
    }

}
