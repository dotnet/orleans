using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Hosting;
using Orleans.Internal;
using Orleans.Runtime;
using Orleans.Runtime.Internal;
using Orleans.Runtime.Scheduler;

namespace Orleans.ScheduledJobs;

/// <inheritdoc/>
internal partial class LocalScheduledJobManager : SystemTarget, ILocalScheduledJobManager, ILifecycleParticipant<ISiloLifecycle>
{
    private readonly JobShardManager _shardManager;
    private readonly IAsyncEnumerable<ClusterMembershipSnapshot> _clusterMembershipUpdates;
    private readonly ILogger<LocalScheduledJobManager> _logger;
    private readonly ScheduledJobsOptions _options;
    private readonly CancellationTokenSource _cts = new();
    private Task? _listenForClusterChangesTask = null;
    private Task? _periodicCheckTask = null;
    private readonly ConcurrentDictionary<string, IJobShard> _shardCache = new();
    private readonly ConcurrentDictionary<DateTimeOffset, IJobShard> _writeableShards = new();
    private readonly ConcurrentDictionary<string, Task> _runningShards = new();
    private readonly SemaphoreSlim _jobConcurrencyLimiter;
    private readonly SemaphoreSlim _shardCreationLock = new(1, 1);

    private static readonly IDictionary<string, string> EmptyMetadata = new Dictionary<string, string>();

    public LocalScheduledJobManager(
        JobShardManager shardManager,
        IClusterMembershipService clusterMembership,
        IOptions<ScheduledJobsOptions> options,
        SystemTargetShared shared,
        ILogger<LocalScheduledJobManager> logger)
        : base(SystemTargetGrainId.CreateGrainType("job-manager"), shared)
    {
        _shardManager = shardManager;
        _clusterMembershipUpdates = clusterMembership.MembershipUpdates;
        _logger = logger;
        _options = options.Value;
        _jobConcurrencyLimiter = new SemaphoreSlim(_options.MaxConcurrentJobsPerSilo);
    }

    /// <inheritdoc/>
    public async Task<ScheduledJob> ScheduleJobAsync(GrainId target, string jobName, DateTimeOffset dueTime, IReadOnlyDictionary<string, string>? metadata, CancellationToken cancellationToken)
    {
        LogSchedulingJob(_logger, jobName, target, dueTime);
        
        var key = GetShardKey(dueTime);

        ScheduledJob? job = null;
        while (job is null)
        {
            if (_writeableShards.TryGetValue(key, out var jobShard))
            {
                job =  await jobShard.TryScheduleJobAsync(target, jobName, dueTime, metadata, cancellationToken);
                if (job is not null)
                {
                    LogJobScheduled(_logger, jobName, job.Id, jobShard.Id, target);
                    return job;
                }
            }

            // No available shard found, create a new one
            // TODO: Use a more fine-grained locking mechanism (e.g., per-shard-key lock) to avoid blocking all shard creations
            await _shardCreationLock.WaitAsync(cancellationToken);
            try
            {
                // Double-check if shard was created while waiting for lock
                if (_writeableShards.TryGetValue(key, out jobShard))
                {
                    job = await jobShard.TryScheduleJobAsync(target, jobName, dueTime, metadata, cancellationToken);
                    if (job is not null)
                    {
                        LogJobScheduled(_logger, jobName, job.Id, jobShard.Id, target);
                        return job;
                    }
                }

                // Always assign to creator
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
                var newShard = await _shardManager.CreateShardAsync(key, key.Add(_options.ShardDuration), EmptyMetadata, linkedCts.Token);
                LogCreatingNewShard(_logger, key);
                _writeableShards[key] = newShard;
                _shardCache.TryAdd(newShard.Id, newShard);
                StartRunningShardTracked(newShard);
            }
            finally
            {
                _shardCreationLock.Release();
            }
        }
        // This point should never be reached
        throw new InvalidOperationException("Failed to schedule job");
    }

    public void Participate(ISiloLifecycle lifecycle)
    {
        lifecycle.Subscribe(
            nameof(LocalScheduledJobManager),
            ServiceLifecycleStage.Active,
            ct => Start(ct),
            ct => Stop(ct));
    }

    private Task Start(CancellationToken ct)
    {
        LogStarting(_logger);

        using (var _ = new ExecutionContextSuppressor())
        {
            _listenForClusterChangesTask = Task.Factory.StartNew(
                state => ((LocalScheduledJobManager)state!).ProcessMembershipUpdates(),
                this,
                CancellationToken.None,
                TaskCreationOptions.None,
                WorkItemGroup.TaskScheduler).Unwrap();
            _listenForClusterChangesTask.Ignore();

            _periodicCheckTask = Task.Factory.StartNew(
                state => ((LocalScheduledJobManager)state!).PeriodicShardCheck(),
                this,
                CancellationToken.None,
                TaskCreationOptions.None,
                WorkItemGroup.TaskScheduler).Unwrap();
            _periodicCheckTask.Ignore();
        }

        LogStarted(_logger);
        return Task.CompletedTask;
    }

    private async Task Stop(CancellationToken ct)
    {
        LogStopping(_logger, _runningShards.Count);
        
        _cts.Cancel();
        
        if (_listenForClusterChangesTask is not null)
        {
            await _listenForClusterChangesTask;
            //_listenForClusterChangesTask = null;
        }

        if (_periodicCheckTask is not null)
        {
            await _periodicCheckTask;
        }
        
        await Task.WhenAll(_runningShards.Values.ToArray());
        
        // Dispose any remaining shards in the cache
        foreach (var shard in _shardCache.Values)
        {
            try
            {
                await shard.DisposeAsync();
            }
            catch (Exception ex)
            {
                LogErrorDisposingShard(_logger, ex, shard.Id);
            }
        }
        
        LogStopped(_logger);
    }

    private async Task ProcessMembershipUpdates()
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding | ConfigureAwaitOptions.ContinueOnCapturedContext);
        var current = new HashSet<SiloAddress>();
        await foreach (var membershipSnapshot in _clusterMembershipUpdates.WithCancellation(_cts.Token))
        {
            try
            {
                // Get active members.
                var update = new HashSet<SiloAddress>(membershipSnapshot.Members.Values
                    .Where(member => member.Status == SiloStatus.Active)
                    .Select(member => member.SiloAddress));

                // If active list has changed, track new list and notify.
                if (!current.SetEquals(update))
                {
                    current = update;
                    await GetUnassignedShards();
                }
            }
            catch (Exception exception)
            {
                LogErrorProcessingClusterMembership(_logger, exception);
            }
        }
    }

    private async Task GetUnassignedShards()
    {
        LogCheckingForUnassignedShards(_logger);
        
        var shards = await _shardManager.AssignJobShardsAsync(DateTime.UtcNow.AddHours(1), _cts.Token);
        if (shards.Count > 0)
        {
            LogAssignedShards(_logger, shards.Count);
            foreach (var shard in shards)
            {
                // Add to cache so periodic check can find it
                _shardCache.TryAdd(shard.Id, shard);
                
                // Only start the shard if it's not already running
                if (!_runningShards.ContainsKey(shard.Id))
                {
                    StartRunningShardTracked(shard);
                }
            }
        }
        else
        {
            LogNoShardsToAssign(_logger);
        }
    }

    private void StartRunningShardTracked(IJobShard shard)
    {
        // Only start if not already running
        if (_runningShards.ContainsKey(shard.Id))
        {
            return;
        }

        // Only start if it's time to start (within buffer period)
        if (!ShouldStartShardNow(shard))
        {
            LogShardNotReadyYet(_logger, shard.Id, shard.StartTime);
            return;
        }

        LogStartingShard(_logger, shard.Id, shard.StartTime, shard.EndTime);
        _runningShards.TryAdd(shard.Id, RunShard(shard));
    }

    private bool ShouldStartShardNow(IJobShard shard)
    {
        var activationTime = shard.StartTime.Subtract(_options.ShardActivationBufferPeriod);
        return DateTimeOffset.UtcNow >= activationTime;
    }

    private async Task PeriodicShardCheck()
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding | ConfigureAwaitOptions.ContinueOnCapturedContext);

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));

        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(_cts.Token);

                LogCheckingPendingShards(_logger);
                foreach (var shard in _shardCache.Values)
                {
                    StartRunningShardTracked(shard);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                LogErrorInPeriodicCheck(_logger, ex);
            }
        }
    }

    private async Task RunShard(IJobShard shard)
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
                await Task.Delay(delay, _cts.Token); 
            }

            LogBeginProcessingShard(_logger, shard.Id);

            // Process all jobs in the shard
            await foreach (var jobContext in shard.ConsumeScheduledJobsAsync().WithCancellation(_cts.Token))
            {
                // Wait for concurrency slot
                await _jobConcurrencyLimiter.WaitAsync(_cts.Token);
                // Start processing the job. RunJob will release the semaphore when done and remove itself from the tasks dictionary
                tasks[jobContext.Job.Id] = RunJob(jobContext, shard, tasks);
            }

            LogCompletedProcessingShard(_logger, shard.Id);
            
            // Unregister the shard
            try
            {
                await _shardManager.UnregisterShardAsync(shard, _cts.Token);
                LogUnregisteredShard(_logger, shard.Id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogErrorUnregisteringShard(_logger, ex, shard.Id);
            }
        }
        catch (OperationCanceledException)
        {
            LogShardCancelled(_logger, shard.Id);
        }
        finally
        {
            // Dispose the shard to clean up any resources (e.g., background tasks, channels)
            try
            {
                _shardCache.TryRemove(shard.Id, out _);
                await Task.WhenAll(tasks.Values);
                await shard.DisposeAsync();
                _runningShards.TryRemove(shard.Id, out _);
            }
            catch (Exception ex)
            {
                LogErrorDisposingShard(_logger, ex, shard.Id);
            }
        }
    }

    private async Task RunJob(IScheduledJobContext jobContext, IJobShard shard, ConcurrentDictionary<string, Task> runningTasks)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.ForceYielding);

        try
        {
            LogExecutingJob(_logger, jobContext.Job.Id, jobContext.Job.Name, jobContext.Job.TargetGrainId, jobContext.Job.DueTime);

            var target = this.RuntimeClient.InternalGrainFactory
            .GetGrain(jobContext.Job.TargetGrainId)
            .AsReference<IScheduledJobReceiverExtension>();
            
            await target.DeliverScheduledJobAsync(jobContext, _cts.Token);
            await shard.RemoveJobAsync(jobContext.Job.Id, _cts.Token);

            LogJobExecutedSuccessfully(_logger, jobContext.Job.Id, jobContext.Job.Name);
        }
        catch (Exception ex) when (ex is not TaskCanceledException)
        {
            LogErrorExecutingJob(_logger, ex, jobContext.Job.Id);
            var retryTime = _options.ShouldRetry(jobContext, ex);
            if (retryTime is not null)
            {
                LogRetryingJob(_logger, jobContext.Job.Id, jobContext.Job.Name, retryTime.Value, jobContext.DequeueCount);
                await shard.RetryJobLaterAsync(jobContext, retryTime.Value, _cts.Token);
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

    /// <inheritdoc/>
    public async Task<bool> TryCancelScheduledJobAsync(ScheduledJob job, CancellationToken cancellationToken)
    {
        LogCancellingJob(_logger, job.Id, job.Name, job.ShardId);
        
        if (!_shardCache.TryGetValue(job.ShardId, out var shard))
        {
            LogJobCancellationFailed(_logger, job.Id, job.Name, job.ShardId);
            return false;
        }

        // Use a linked cancellation token that combines the provided token with the internal one
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        var wasRemoved = await shard.RemoveJobAsync(job.Id, linkedCts.Token);
        LogJobCancelled(_logger, job.Id, job.Name, job.ShardId);
        return wasRemoved;
    }

    /// <summary>
    /// Calculates the shard key for a scheduled time using epoch-based bucketing.
    /// This ensures all times within the same shard duration window map to the same key,
    /// regardless of the configured <see cref="ScheduledJobsOptions.ShardDuration"/>.
    /// </summary>
    /// <param name="scheduledTime">The time when the job is scheduled to run.</param>
    /// <returns>The UTC start time of the shard bucket containing the scheduled time.</returns>
    /// <example>
    /// For ShardDuration = 1 hour:
    /// - 14:37:25 -> 14:00:00
    /// - 14:59:59 -> 14:00:00
    /// - 15:00:00 -> 15:00:00
    /// 
    /// For ShardDuration = 15 minutes:
    /// - 14:37:25 -> 14:30:00
    /// - 14:44:59 -> 14:30:00
    /// - 14:45:00 -> 14:45:00
    /// </example>
    private DateTimeOffset GetShardKey(DateTimeOffset scheduledTime)
    {
        // Calculate which time bucket the scheduled time falls into using integer division.
        // This works for any duration (minutes, hours, days) and guarantees consistent
        // shard alignment across all silos in the cluster.
        var shardDurationTicks = _options.ShardDuration.Ticks;
        var epochTicks = scheduledTime.UtcTicks;
        var bucketTicks = (epochTicks / shardDurationTicks) * shardDurationTicks;
        return new DateTimeOffset(bucketTicks, TimeSpan.Zero);
    }
}
