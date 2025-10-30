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
    private readonly ConcurrentDictionary<DateTimeOffset, ConcurrentDictionary<string, IJobShard>> _shardCache = new();
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
        var shards = _shardCache.GetOrAdd(key, _ => new ConcurrentDictionary<string, IJobShard>());
        // Find a shard that can accept this job
        // TODO more efficient lookup
        foreach (var shard in shards.Select(s => s.Value))
        {
            if (!shard.IsComplete && shard.StartTime <= dueTime && shard.EndTime >= dueTime && await shard.GetJobCount() <= MaxJobCountPerShard)
            {
                var job = await shard.TryScheduleJobAsync(target, jobName, dueTime, metadata, cancellationToken);
                if (job is not null)
                {
                    LogJobScheduled(_logger, jobName, job.Id, shard.Id, target);
                    return job;
                }
            }
        }
        
        // No available shard found, create a new one, assigning it to this silo if the shard start time is near
        var assignToMe = key.Add(TimeSpan.FromMinutes(5)) > DateTimeOffset.UtcNow;
        LogCreatingNewShard(_logger, key, assignToMe);
        
        // Use a linked cancellation token that combines the provided token with the internal one
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        var newShard = await _shardManager.RegisterShard(this.Silo, key, key.Add(_options.ShardDuration), EmptyMetadata, assignToMe, linkedCts.Token);
        shards.TryAdd(newShard.Id, newShard);
        var scheduledJob = await newShard.TryScheduleJobAsync(target, jobName, dueTime, metadata, linkedCts.Token);
        
        if (scheduledJob is null)
        {
            throw new InvalidOperationException("Failed to schedule job on newly created shard.");
        }

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
        
        // Dispose any remaining shards in the cache
        foreach (var shardGroup in _shardCache.Values)
        {
            foreach (var shard in shardGroup.Values)
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
        }
        
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

    private void StartRunningShardTracked(IJobShard shard)
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

    private async Task RunShard(IJobShard shard)
    {
        var tasks = new ConcurrentDictionary<string, Task>();
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
                // Wait for concurrency slot
                await _jobConcurrencyLimiter.WaitAsync(_cts.Token);
                // Start processing the job. RunJob will release the semaphore when done and remove itself from the tasks dictionary
                tasks[jobContext.Job.Id] = RunJob(jobContext, shard, tasks);
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
        finally
        {
            // Dispose the shard to clean up any resources (e.g., background tasks, channels)
            try
            {
                _shardCache.TryRemove(GetShardKey(shard.StartTime), out _);
                await Task.WhenAll(tasks.Values);
                await shard.DisposeAsync();
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
    public async Task<bool> TryCancelScheduledJobAsync(IScheduledJob job, CancellationToken cancellationToken)
    {
        LogCancellingJob(_logger, job.Id, job.Name, job.ShardId);
        
        var key = GetShardKey(job.DueTime);
        if (!_shardCache.TryGetValue(key, out var shards) || !shards.TryGetValue(job.ShardId, out var shard))
        {
            LogJobCancellationFailed(_logger, job.Id, job.Name, job.ShardId);
            return false;
        }

        // Use a linked cancellation token that combines the provided token with the internal one
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        await shard.RemoveJobAsync(job.Id, linkedCts.Token);
        LogJobCancelled(_logger, job.Id, job.Name, job.ShardId);
        return true;
    }

    private static DateTimeOffset GetShardKey(DateTimeOffset scheduledTime)
    {
        return new DateTime(scheduledTime.Year, scheduledTime.Month, scheduledTime.Day, scheduledTime.Hour, scheduledTime.Minute, 0);
    }
}
