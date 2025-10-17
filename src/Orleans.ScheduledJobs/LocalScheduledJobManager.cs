using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Hosting;
using Orleans.Runtime;
using Orleans.Runtime.Scheduler;

namespace Orleans.ScheduledJobs;

public interface ILocalScheduledJobManager
{
    Task<IScheduledJob> ScheduleJobAsync(GrainId target, string jobName, DateTimeOffset dueTime);

    Task<bool> TryCancelScheduledJobAsync(IScheduledJob job);
}

internal class LocalScheduledJobManager : SystemTarget, ILocalScheduledJobManager, ILifecycleParticipant<ISiloLifecycle>
{
    private readonly JobShardManager _shardManager;
    private readonly ILogger<LocalScheduledJobManager> _logger;
    private readonly ScheduledJobsOptions _options;
    private CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<DateTimeOffset, ConcurrentBag<JobShard>> _shardCache = new();
    private readonly int MaxJobCountPerShard = 1000;

    private static readonly IDictionary<string, string> EmptyMetadata = new Dictionary<string, string>();

    public LocalScheduledJobManager(JobShardManager shardManager, IOptions<ScheduledJobsOptions> options, SystemTargetShared shared, ILogger<LocalScheduledJobManager> logger)
        : base(SystemTargetGrainId.CreateGrainType("scheduledjobs-manager"), shared)
    {
        _shardManager = shardManager;
        _logger = logger;
        _options = options.Value;
    }

    public async Task<IScheduledJob> ScheduleJobAsync(GrainId target, string jobName, DateTimeOffset dueTime)
    {
        var key = GetShardKey(dueTime);
        // Try to get all the available shards for this key
        var shards = _shardCache.GetOrAdd(key, _ => new ConcurrentBag<JobShard>());
        // Find a shard that can accept this job
        // TODO more efficient lookup
        foreach (var shard in shards)
        {
            if (!shard.IsComplete && shard.StartTime <= dueTime && shard.EndTime >= dueTime && await shard.GetJobCount() <= MaxJobCountPerShard)
            {
                return await shard.ScheduleJobAsync(target, jobName, dueTime);
            }
        }
        // No available shard found, create a new one
        var newShard = await _shardManager.RegisterShard(this.Silo, key, key.Add(_options.ShardDuration), EmptyMetadata);
        shards.Add(newShard);
        var job = await newShard.ScheduleJobAsync(target, jobName, dueTime);
        this.QueueTask(() => RunShard(newShard)).Ignore();
        return job;
    }

    public void Participate(ISiloLifecycle lifecycle)
    {
        lifecycle.Subscribe(
            nameof(LocalScheduledJobManager),
            ServiceLifecycleStage.Active,
            ct => Start(ct),
            ct => Stop(ct));
    }

    private Task Stop(CancellationToken ct)
    {
        // TODO Wait for running shards to complete
        _cts.Cancel();
        return Task.CompletedTask;
    }

    private Task Start(CancellationToken ct)
    {
        this.QueueTask(WatchForShardsAsync);
        return Task.CompletedTask;
    }

    private async Task WatchForShardsAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var shards = await _shardManager.GetJobShardsAsync(this.Silo, DateTime.UtcNow.AddHours(1));
                if (shards.Count > 0)
                {
                    foreach (var shard in shards)
                    {
                        RunShard(shard).Ignore(); // TODO: keep track of running shaFrds
                    }
                }
                await Task.Delay(TimeSpan.FromMinutes(1), _cts.Token);
            }
        }
        catch (TaskCanceledException)
        {
            // Ignore, shutting down
        }
    }

    private async Task RunShard(JobShard shard)
    {
        // do not start a shard before its start time, check every minute?
        try
        {
            if (shard.StartTime > DateTime.UtcNow)
            {
                // Wait until the shard's start time
                var delay = shard.StartTime - DateTimeOffset.Now;
                await Task.Delay(delay, _cts.Token); // max time for delay is ~24 days???
            }

            // Process all jobs in the shard
            await foreach (var jobContext in shard.ConsumeScheduledJobsAsync().WithCancellation(_cts.Token))
            {
                try
                {
                    // Parallel.ForEachAsync would be nice here
                    // or SemaphoreSlim to limit concurrency
                    // TODO: Do it in parallel, with concurrency limit
                    // Use Poly for retries? put it back in the shard on failure?
                    var target = this.RuntimeClient.InternalGrainFactory
                        .GetGrain(jobContext.Job.TargetGrainId)
                        .AsReference<IScheduledJobReceiverExtension>();
                    await target.DeliverScheduledJobAsync(jobContext, new CancellationToken());
                    await shard.RemoveJobAsync(jobContext.Job.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error executing job {JobId}", jobContext.Job.Id);
                    var retryTime = _options.ShouldRetry(jobContext, ex);
                    if (retryTime != null)
                    {
                        // TODO
                    }
                }
            }
            // Unregister the shard
            await _shardManager.UnregisterShard(this.Silo, shard);
        }
        catch (TaskCanceledException)
        {
            // Ignore, shutting down
        }
    }

    private DateTimeOffset GetShardKey(DateTimeOffset scheduledTime)
    {
        return new DateTime(scheduledTime.Year, scheduledTime.Month, scheduledTime.Day, scheduledTime.Hour, scheduledTime.Minute, 0);
    }

    public Task<bool> TryCancelScheduledJobAsync(IScheduledJob job)
    {
        // TODO: Implement job cancellation
        return Task.FromResult(false);
    }
}
