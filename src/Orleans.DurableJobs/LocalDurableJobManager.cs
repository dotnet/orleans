using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

namespace Orleans.DurableJobs;

/// <inheritdoc/>
internal partial class LocalDurableJobManager : SystemTarget, ILocalDurableJobManager, ILifecycleParticipant<ISiloLifecycle>
{
    private readonly JobShardManager _shardManager;
    private readonly ShardExecutor _shardExecutor;
    private readonly IAsyncEnumerable<ClusterMembershipSnapshot> _clusterMembershipUpdates;
    private readonly ILogger<LocalDurableJobManager> _logger;
    private readonly DurableJobsOptions _options;
    private readonly CancellationTokenSource _cts = new();
    private Task? _listenForClusterChangesTask;
    private Task? _periodicCheckTask;

    // Shard tracking state
    private readonly ConcurrentDictionary<string, IJobShard> _shardCache = new();
    private readonly ConcurrentDictionary<DateTimeOffset, IJobShard> _writeableShards = new();
    private readonly ConcurrentDictionary<string, Task> _runningShards = new();
    private readonly SemaphoreSlim _shardCreationLock = new(1, 1);
    private readonly SemaphoreSlim _shardCheckSignal = new(0);

    private static readonly IDictionary<string, string> EmptyMetadata = new Dictionary<string, string>();

    public LocalDurableJobManager(
        JobShardManager shardManager,
        ShardExecutor shardExecutor,
        IClusterMembershipService clusterMembership,
        IOptions<DurableJobsOptions> options,
        SystemTargetShared shared,
        ILogger<LocalDurableJobManager> logger)
        : base(SystemTargetGrainId.CreateGrainType("job-manager"), shared)
    {
        _shardManager = shardManager;
        _shardExecutor = shardExecutor;
        _clusterMembershipUpdates = clusterMembership.MembershipUpdates;
        _logger = logger;
        _options = options.Value;
    }

    /// <inheritdoc/>
    public async Task<DurableJob> ScheduleJobAsync(GrainId target, string jobName, DateTimeOffset dueTime, IReadOnlyDictionary<string, string>? metadata, CancellationToken cancellationToken)
    {
        LogSchedulingJob(_logger, jobName, target, dueTime);

        var shardKey = GetShardKey(dueTime);

        while (true)
        {
            // Fast path: shard already exists
            if (_writeableShards.TryGetValue(shardKey, out var existingShard))
            {
                var job = await existingShard.TryScheduleJobAsync(target, jobName, dueTime, metadata, cancellationToken);
                if (job is not null)
                {
                    LogJobScheduled(_logger, jobName, job.Id, existingShard.Id, target);
                    return job;
                }

                // Shard is full or no longer writable, remove from writable shards and try again
                _writeableShards.TryRemove(shardKey, out _);
                continue;
            }

            // Slow path: need to create shard
            await _shardCreationLock.WaitAsync(cancellationToken);
            try
            {
                // Double-check after acquiring lock
                if (_writeableShards.TryGetValue(shardKey, out existingShard))
                {
                    continue;
                }

                // Create new shard
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
                var endTime = shardKey.Add(_options.ShardDuration);
                var newShard = await _shardManager.CreateShardAsync(shardKey, endTime, EmptyMetadata, linkedCts.Token);

                LogCreatingNewShard(_logger, shardKey);
                _writeableShards[shardKey] = newShard;
                _shardCache.TryAdd(newShard.Id, newShard);
                TryActivateShard(newShard);
            }
            finally
            {
                _shardCreationLock.Release();
            }
        }
    }

    public void Participate(ISiloLifecycle lifecycle)
    {
        lifecycle.Subscribe(
            nameof(LocalDurableJobManager),
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
                state => ((LocalDurableJobManager)state!).ProcessMembershipUpdates(),
                this,
                CancellationToken.None,
                TaskCreationOptions.None,
                WorkItemGroup.TaskScheduler).Unwrap();
            _listenForClusterChangesTask.Ignore();

            _periodicCheckTask = Task.Factory.StartNew(
                state => ((LocalDurableJobManager)state!).PeriodicShardCheck(),
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
            await _listenForClusterChangesTask.SuppressThrowing();
        }

        if (_periodicCheckTask is not null)
        {
            await _periodicCheckTask.SuppressThrowing();
        }

        await Task.WhenAll(_runningShards.Values.ToArray());

        LogStopped(_logger);
    }

    /// <inheritdoc/>
    public async Task<bool> TryCancelDurableJobAsync(DurableJob job, CancellationToken cancellationToken)
    {
        LogCancellingJob(_logger, job.Id, job.Name, job.ShardId);

        if (!_shardCache.TryGetValue(job.ShardId, out var shard))
        {
            LogJobCancellationFailed(_logger, job.Id, job.Name, job.ShardId);
            return false;
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
        var wasRemoved = await shard.RemoveJobAsync(job.Id, linkedCts.Token);
        LogJobCancelled(_logger, job.Id, job.Name, job.ShardId);
        return wasRemoved;
    }

    private async Task ProcessMembershipUpdates()
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding | ConfigureAwaitOptions.ContinueOnCapturedContext);
        var current = new HashSet<SiloAddress>();

        try
        {
            await foreach (var membershipSnapshot in _clusterMembershipUpdates.WithCancellation(_cts.Token))
            {
                try
                {
                    // Get active members
                    var update = new HashSet<SiloAddress>(membershipSnapshot.Members.Values
                        .Where(member => member.Status == SiloStatus.Active)
                        .Select(member => member.SiloAddress));

                    // If active list has changed, trigger immediate shard check
                    if (!current.SetEquals(update))
                    {
                        current = update;
                        _shardCheckSignal.Release();
                    }
                }
                catch (Exception exception)
                {
                    LogErrorProcessingClusterMembership(_logger, exception);
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (!_cts.Token.IsCancellationRequested)
            {
                throw;
            }
        }
    }

    private async Task PeriodicShardCheck()
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding | ConfigureAwaitOptions.ContinueOnCapturedContext);

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));

        Task timerTask = Task.CompletedTask;
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                // Wait for either periodic timer OR signal from membership changes
                if (timerTask.IsCompleted)
                {
                    timerTask = timer.WaitForNextTickAsync(_cts.Token).AsTask();
                }

                var signalTask = _shardCheckSignal.WaitAsync(_cts.Token);
                await Task.WhenAny(timerTask, signalTask);

                LogCheckingPendingShards(_logger);

                // Clean up old writable shards that have passed their time window
                var now = DateTimeOffset.UtcNow;
                foreach (var key in _writeableShards.Keys.ToArray())
                {
                    var shardEndTime = key.Add(_options.ShardDuration);
                    if (shardEndTime < now)
                    {
                        _writeableShards.TryRemove(key, out _);
                    }
                }

                // Query ShardManager for assigned shards (source of truth)
                var shards = await _shardManager.AssignJobShardsAsync(DateTime.UtcNow.AddHours(1), _cts.Token);
                if (shards.Count > 0)
                {
                    LogAssignedShards(_logger, shards.Count);
                    foreach (var shard in shards)
                    {
                        _shardCache.TryAdd(shard.Id, shard);

                        if (!_runningShards.ContainsKey(shard.Id))
                        {
                            TryActivateShard(shard);
                        }
                    }
                }
                else
                {
                    LogNoShardsToAssign(_logger);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                LogErrorInPeriodicCheck(_logger, ex);
                await Task.Delay(TimeSpan.FromSeconds(5), _cts.Token).SuppressThrowing();
            }
        }
    }

    private void TryActivateShard(IJobShard shard)
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

        if (_runningShards.TryAdd(shard.Id, Task.CompletedTask))
        {
            LogStartingShard(_logger, shard.Id, shard.StartTime, shard.EndTime);
            _runningShards[shard.Id] = RunShardWithCleanupAsync(shard);
        }
    }

    private async Task RunShardWithCleanupAsync(IJobShard shard)
    {
        try
        {
            await _shardExecutor.RunShardAsync(shard, _cts.Token);

            // Unregister the shard from the manager
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
        finally
        {
            // Clean up tracking and dispose the shard
            _shardCache.TryRemove(shard.Id, out _);
            _runningShards.TryRemove(shard.Id, out _);

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

    private bool ShouldStartShardNow(IJobShard shard)
    {
        var activationTime = shard.StartTime.Subtract(_options.ShardActivationBufferPeriod);
        return DateTimeOffset.UtcNow >= activationTime;
    }

    private DateTimeOffset GetShardKey(DateTimeOffset scheduledTime)
    {
        var shardDurationTicks = _options.ShardDuration.Ticks;
        var epochTicks = scheduledTime.UtcTicks;
        var bucketTicks = (epochTicks / shardDurationTicks) * shardDurationTicks;
        return new DateTimeOffset(bucketTicks, TimeSpan.Zero);
    }
}
