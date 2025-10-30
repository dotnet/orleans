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
using Orleans.Runtime;
using Orleans.Runtime.Internal;
using Orleans.Runtime.Scheduler;

namespace Orleans.ScheduledJobs;

/// <summary>
/// Provides functionality for scheduling and managing jobs on the local silo.
/// </summary>
public interface ILocalScheduledJobManager
{
    /// <summary>
    /// Schedules a job to be executed at a specific time on the target grain.
    /// </summary>
    /// <param name="target">The grain identifier of the target grain that will receive the scheduled job.</param>
    /// <param name="jobName">The name of the job for identification purposes.</param>
    /// <param name="dueTime">The date and time when the job should be executed.</param>
    /// <param name="metadata">Optional metadata associated with the job.</param>
    /// <param name="cancellationToken">A cancellation token to cancel the operation.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation that returns the scheduled job.</returns>
    Task<IScheduledJob> ScheduleJobAsync(GrainId target, string jobName, DateTimeOffset dueTime, IReadOnlyDictionary<string, string>? metadata, CancellationToken cancellationToken);

    /// <summary>
    /// Attempts to cancel a previously scheduled job.
    /// </summary>
    /// <param name="job">The scheduled job to cancel.</param>
    /// <param name="cancellationToken">A cancellation token to cancel the operation.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation that returns <see langword="true"/> if the job was successfully canceled; otherwise, <see langword="false"/>.</returns>
    Task<bool> TryCancelScheduledJobAsync(IScheduledJob job, CancellationToken cancellationToken);
}

/// <inheritdoc/>
internal partial class LocalScheduledJobManager : SystemTarget, ILocalScheduledJobManager, ILifecycleParticipant<ISiloLifecycle>
{
    private readonly JobShardManager _shardManager;
    private readonly IAsyncEnumerable<ClusterMembershipSnapshot> _clusterMembershipUpdates;
    private readonly ILogger<LocalScheduledJobManager> _logger;
    private readonly ScheduledJobsOptions _options;
    private CancellationTokenSource _cts = new();
    private Task? _listenForClusterChangesTask = null;
    private Task? _watchForShardtoStartTask = null;
    private readonly ConcurrentDictionary<DateTimeOffset, ConcurrentDictionary<string, JobShard>> _shardCache = new();
    private readonly ConcurrentDictionary<string, Task> _runningShards = new();
    private readonly int MaxJobCountPerShard = 1000;
    private readonly SemaphoreSlim _jobConcurrencyLimiter;

    private static readonly IDictionary<string, string> EmptyMetadata = new Dictionary<string, string>();

    public LocalScheduledJobManager(
        JobShardManager shardManager,
        IClusterMembershipService clusterMembership,
        IOptions<ScheduledJobsOptions> options,
        SystemTargetShared shared,
        ILogger<LocalScheduledJobManager> logger)
        : base(SystemTargetGrainId.CreateGrainType("scheduledjobs-manager"), shared)
    {
        _shardManager = shardManager;
        _clusterMembershipUpdates = clusterMembership.MembershipUpdates;
        _logger = logger;
        _options = options.Value;
        _jobConcurrencyLimiter = new SemaphoreSlim(_options.MaxConcurrentJobsPerSilo);
    }

    /// <inheritdoc/>
    public async Task<IScheduledJob> ScheduleJobAsync(GrainId target, string jobName, DateTimeOffset dueTime, IReadOnlyDictionary<string, string>? metadata, CancellationToken cancellationToken)
    {
        LogSchedulingJob(_logger, jobName, target, dueTime);
        
        var key = GetShardKey(dueTime);
        // Try to get all the available shards for this key
        var shards = _shardCache.GetOrAdd(key, _ => new ConcurrentDictionary<string, JobShard>());
        // Find a shard that can accept this job
        // TODO more efficient lookup
        foreach (var shard in shards.Select(s => s.Value))
        {
            if (!shard.IsComplete && shard.StartTime <= dueTime && shard.EndTime >= dueTime && await shard.GetJobCount() <= MaxJobCountPerShard)
            {
                var job = await shard.ScheduleJobAsync(target, jobName, dueTime, metadata);
                LogJobScheduled(_logger, jobName, job.Id, shard.Id, target);
                return job;
            }
        }
        
        // No available shard found, create a new one, assigning it to this silo if the shard start time is near
        var assignToMe = key.Add(TimeSpan.FromMinutes(5)) > DateTimeOffset.UtcNow;
        LogCreatingNewShard(_logger, key, assignToMe);
        
        // Use a linked cancellation token that combines the provided token with the internal one
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        var newShard = await _shardManager.RegisterShard(this.Silo, key, key.Add(_options.ShardDuration), EmptyMetadata, assignToMe, linkedCts.Token);
        shards.TryAdd(newShard.Id, newShard);
        var scheduledJob = await newShard.ScheduleJobAsync(target, jobName, dueTime, metadata);
        
        LogJobScheduled(_logger, jobName, scheduledJob.Id, newShard.Id, target);
        
        if (assignToMe)
        {
            StartRunningShardTracked(newShard);
        }
        
        return scheduledJob;
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
        }
        
        LogStarted(_logger);
        return Task.CompletedTask;
    }

    private async Task Stop(CancellationToken ct)
    {
        LogStopping(_logger, _runningShards.Count);
        
        _cts.Cancel();
        if (_watchForShardtoStartTask is not null)
        {
            await _watchForShardtoStartTask;
            _watchForShardtoStartTask = null;
        }
        
        if (_listenForClusterChangesTask is not null)
        {
            await _listenForClusterChangesTask;
            _listenForClusterChangesTask = null;
        }
        
        await Task.WhenAll(_runningShards.Values.ToArray());
        
        LogStopped(_logger);
    }

    private async Task ProcessMembershipUpdates()
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
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
        
        var shards = await _shardManager.AssignJobShardsAsync(this.Silo, DateTime.UtcNow.AddHours(1), _cts.Token);
        if (shards.Count > 0)
        {
            LogAssignedShards(_logger, shards.Count);
            foreach (var shard in shards)
            {
                StartRunningShardTracked(shard);
            }
        }
        else
        {
            LogNoShardsToAssign(_logger);
        }
    }

    private void StartRunningShardTracked(JobShard shard)
    {
        LogStartingShard(_logger, shard.Id, shard.StartTime, shard.EndTime);
        
        var shardTask = Task.Run(async () =>
        {
            try
            {
                await RunShard(shard);
            }
            finally
            {
                _runningShards.TryRemove(shard.Id, out _);
            }
        });
        
        _runningShards.TryAdd(shard.Id, shardTask);
    }

    private async Task RunShard(JobShard shard)
    {
        try
        {
            if (shard.StartTime > DateTime.UtcNow)
            {
                // Wait until the shard's start time
                var delay = shard.StartTime - DateTimeOffset.Now;
                LogWaitingForShardStartTime(_logger, shard.Id, delay, shard.StartTime);
                await Task.Delay(delay, _cts.Token); 
            }

            LogBeginProcessingShard(_logger, shard.Id);

            // Process all jobs in the shard
            await foreach (var jobContext in shard.ConsumeScheduledJobsAsync().WithCancellation(_cts.Token))
            {
                try
                {
                    await _jobConcurrencyLimiter.WaitAsync(_cts.Token);
                    LogExecutingJob(_logger, jobContext.Job.Id, jobContext.Job.Name, jobContext.Job.TargetGrainId, jobContext.Job.DueTime);
                    
                    var target = this.RuntimeClient.InternalGrainFactory
                        .GetGrain(jobContext.Job.TargetGrainId)
                        .AsReference<IScheduledJobReceiverExtension>();
                    await target.DeliverScheduledJobAsync(jobContext, _cts.Token);
                    await shard.RemoveJobAsync(jobContext.Job.Id);
                    
                    LogJobExecutedSuccessfully(_logger, jobContext.Job.Id, jobContext.Job.Name);
                }
                catch (Exception ex) when (ex is not TaskCanceledException)
                {
                    LogErrorExecutingJob(_logger, ex, jobContext.Job.Id);
                    var retryTime = _options.ShouldRetry(jobContext, ex);
                    if (retryTime is not null)
                    {
                        LogRetryingJob(_logger, jobContext.Job.Id, jobContext.Job.Name, retryTime.Value, jobContext.DequeueCount);
                        await shard.RetryJobLaterAsync(jobContext, retryTime.Value);
                    }
                    else
                    {
                        LogJobFailedNoRetry(_logger, jobContext.Job.Id, jobContext.Job.Name, jobContext.DequeueCount);
                    }
                }
                finally
                {
                    _jobConcurrencyLimiter.Release();
                }
            }
            
            LogCompletedProcessingShard(_logger, shard.Id);
            
            // Unregister the shard
            try
            {
                await _shardManager.UnregisterShard(this.Silo, shard, _cts.Token);
                LogUnregisteredShard(_logger, shard.Id);
            }
            catch (Exception ex) when (ex is not TaskCanceledException)
            {
                LogErrorUnregisteringShard(_logger, ex, shard.Id);
            }
        }
        catch (TaskCanceledException)
        {
            LogShardCancelled(_logger, shard.Id);
        }
    }

    /// <inheritdoc/>
    public async Task<bool> TryCancelScheduledJobAsync(IScheduledJob job, CancellationToken cancellationToken)
    {
        LogCancellingJob(_logger, job.Id, job.Name, job.ShardId);
        
        var key = GetShardKey(job.DueTime);
        if (!_shardCache.TryGetValue(key, out var shards) || !shards.TryGetValue(job.ShardId, out var shard))
        {
            LogJobCancellationFailed(_logger, job.Id, job.Name, job.ShardId);
            return false;
        }
        
        await shard.RemoveJobAsync(job.Id);
        LogJobCancelled(_logger, job.Id, job.Name, job.ShardId);
        return true;
    }

    private static DateTimeOffset GetShardKey(DateTimeOffset scheduledTime)
    {
        return new DateTime(scheduledTime.Year, scheduledTime.Month, scheduledTime.Day, scheduledTime.Hour, scheduledTime.Minute, 0);
    }

    // LoggerMessage definitions
    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Scheduling job '{JobName}' for grain {TargetGrain} at {DueTime}"
    )]
    private static partial void LogSchedulingJob(ILogger logger, string jobName, GrainId targetGrain, DateTimeOffset dueTime);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Job '{JobName}' (ID: {JobId}) scheduled to shard {ShardId} for grain {TargetGrain}"
    )]
    private static partial void LogJobScheduled(ILogger logger, string jobName, string jobId, string shardId, GrainId targetGrain);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Creating new shard for key {ShardKey}, assigned to this silo: {AssignToMe}"
    )]
    private static partial void LogCreatingNewShard(ILogger logger, DateTimeOffset shardKey, bool assignToMe);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "LocalScheduledJobManager starting"
    )]
    private static partial void LogStarting(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "LocalScheduledJobManager started"
    )]
    private static partial void LogStarted(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "LocalScheduledJobManager stopping. Running shards: {RunningShardCount}"
    )]
    private static partial void LogStopping(ILogger logger, int runningShardCount);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "LocalScheduledJobManager stopped"
    )]
    private static partial void LogStopped(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error processing cluster membership update"
    )]
    private static partial void LogErrorProcessingClusterMembership(ILogger logger, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Checking for unassigned shards"
    )]
    private static partial void LogCheckingForUnassignedShards(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Assigned {ShardCount} shard(s)"
    )]
    private static partial void LogAssignedShards(ILogger logger, int shardCount);

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "No unassigned shards found"
    )]
    private static partial void LogNoShardsToAssign(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Starting shard {ShardId} (Start: {StartTime}, End: {EndTime})"
    )]
    private static partial void LogStartingShard(ILogger logger, string shardId, DateTimeOffset startTime, DateTimeOffset endTime);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Waiting {Delay} for shard {ShardId} start time {StartTime}"
    )]
    private static partial void LogWaitingForShardStartTime(ILogger logger, string shardId, TimeSpan delay, DateTimeOffset startTime);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Begin processing shard {ShardId}"
    )]
    private static partial void LogBeginProcessingShard(ILogger logger, string shardId);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Executing job {JobId} (Name: '{JobName}') for grain {TargetGrain}, due at {DueTime}"
    )]
    private static partial void LogExecutingJob(ILogger logger, string jobId, string jobName, GrainId targetGrain, DateTimeOffset dueTime);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Job {JobId} (Name: '{JobName}') executed successfully"
    )]
    private static partial void LogJobExecutedSuccessfully(ILogger logger, string jobId, string jobName);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error executing job {JobId}"
    )]
    private static partial void LogErrorExecutingJob(ILogger logger, Exception exception, string jobId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Retrying job {JobId} (Name: '{JobName}') at {RetryTime}. Dequeue count: {DequeueCount}"
    )]
    private static partial void LogRetryingJob(ILogger logger, string jobId, string jobName, DateTimeOffset retryTime, int dequeueCount);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Job {JobId} (Name: '{JobName}') failed after {DequeueCount} attempts and will not be retried"
    )]
    private static partial void LogJobFailedNoRetry(ILogger logger, string jobId, string jobName, int dequeueCount);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Completed processing shard {ShardId}"
    )]
    private static partial void LogCompletedProcessingShard(ILogger logger, string shardId);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Unregistered shard {ShardId}"
    )]
    private static partial void LogUnregisteredShard(ILogger logger, string shardId);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error unregistering shard {ShardId}"
    )]
    private static partial void LogErrorUnregisteringShard(ILogger logger, Exception exception, string shardId);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Shard {ShardId} processing cancelled"
    )]
    private static partial void LogShardCancelled(ILogger logger, string shardId);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Attempting to cancel job {JobId} (Name: '{JobName}') in shard {ShardId}"
    )]
    private static partial void LogCancellingJob(ILogger logger, string jobId, string jobName, string shardId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Failed to cancel job {JobId} (Name: '{JobName}') - shard {ShardId} not found in cache"
    )]
    private static partial void LogJobCancellationFailed(ILogger logger, string jobId, string jobName, string shardId);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Job {JobId} (Name: '{JobName}') cancelled from shard {ShardId}"
    )]
    private static partial void LogJobCancelled(ILogger logger, string jobId, string jobName, string shardId);
}
