using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
using Orleans.Runtime.Messaging;
using Orleans.Runtime.Scheduler;

namespace Orleans.DurableJobs;

/// <inheritdoc/>
internal partial class LocalDurableJobManager : SystemTarget, ILocalDurableJobManager, ILocalDurableJobManagerSystemTarget, ILifecycleParticipant<ISiloLifecycle>
{
    internal static readonly GrainType JobManagerGrainType = SystemTargetGrainId.CreateGrainType("job-manager");

    private readonly JobShardManager _shardManager;
    private readonly ShardExecutor _shardExecutor;
    private readonly IInternalGrainFactory _grainFactory;
    private readonly IAsyncEnumerable<ClusterMembershipSnapshot> _clusterMembershipUpdates;
    private readonly IOverloadDetector _overloadDetector;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<LocalDurableJobManager> _logger;
    private readonly DurableJobsOptions _options;
    private readonly CancellationTokenSource _cts = new();
    private Task? _listenForClusterChangesTask;
    private Task? _periodicCheckTask;

    // Shard tracking state
    private readonly ConcurrentDictionary<string, IJobShard> _shardCache = new();
    private readonly ConcurrentDictionary<WritableShardKey, IJobShard> _writeableShards = new();
    private readonly ConcurrentDictionary<string, WritableShardKey> _writeableShardKeys = new();
    private readonly ConcurrentDictionary<string, Task> _runningShards = new();
    private readonly SemaphoreSlim _shardCreationLock = new(1, 1);
    private readonly SemaphoreSlim _shardCheckSignal = new(0);

    // Slow-start state
    private long _startTimestamp;
    private int _totalClaimedShards;
    private int _stripeCounter;
    private const string ShardStripeMetadataKey = "stripe";

    public LocalDurableJobManager(
        JobShardManager shardManager,
        ShardExecutor shardExecutor,
        IInternalGrainFactory grainFactory,
        IClusterMembershipService clusterMembership,
        IOverloadDetector overloadDetector,
        TimeProvider timeProvider,
        IOptions<DurableJobsOptions> options,
        SystemTargetShared shared,
        ILogger<LocalDurableJobManager> logger)
        : base(JobManagerGrainType, shared)
    {
        _shardManager = shardManager;
        _shardExecutor = shardExecutor;
        _grainFactory = grainFactory;
        _clusterMembershipUpdates = clusterMembership.MembershipUpdates;
        _overloadDetector = overloadDetector;
        _timeProvider = timeProvider;
        _logger = logger;
        _options = options.Value;
    }

    /// <inheritdoc/>
    public async Task<DurableJob> ScheduleJobAsync(ScheduleJobRequest request, CancellationToken cancellationToken)
    {
        var startTimestamp = _timeProvider.GetTimestamp();
        using var activity = DurableJobsDiagnostics.StartScheduleActivity(in request);
        request = EnsureScheduleRequestHasTraceContext(request, activity);
        try
        {
            LogSchedulingJob(_logger, request.JobName, request.Target, request.DueTime);

            var shardKey = GetWritableShardKey(request);
            DurableJobsInstruments.OnStripeAssigned(shardKey.Stripe);

            while (true)
            {
                // Fast path: shard already exists
                if (_writeableShards.TryGetValue(shardKey, out var existingShard))
                {
                    DurableJob? job;
                    try
                    {
                        job = await existingShard.TryScheduleJobAsync(request, cancellationToken);
                    }
                    catch (ObjectDisposedException ex) when (TryRemoveWritableShard(shardKey, existingShard) || !IsWritableShard(shardKey, existingShard))
                    {
                        LogWritableShardDisposedDuringScheduling(_logger, ex, existingShard.Id, shardKey.StartTime, shardKey.Stripe);
                        continue;
                    }

                    if (job is not null)
                    {
                        var elapsed = _timeProvider.GetElapsedTime(startTimestamp);
                        LogJobScheduled(_logger, request.JobName, job.Id, existingShard.Id, request.Target);
                        DurableJobsInstruments.OnJobScheduled(elapsed);
                        DurableJobsInstruments.OnScheduleJobCallSucceeded(elapsed);
                        DurableJobsDiagnostics.SetScheduledJobTags(activity, job);
                        return job;
                    }

                    // Shard is full or no longer writable, remove from writable shards and try again
                    TryRemoveWritableShard(shardKey, existingShard);
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
                    var endTime = shardKey.StartTime.Add(_options.ShardDuration);
                    var newShard = await _shardManager.CreateShardAsync(shardKey.StartTime, endTime, CreateShardMetadata(shardKey), linkedCts.Token);

                    LogCreatingNewShard(_logger, shardKey.StartTime, shardKey.Stripe);
                    _writeableShards[shardKey] = newShard;
                    _writeableShardKeys[newShard.Id] = shardKey;
                    _shardCache.TryAdd(newShard.Id, newShard);
                    TryActivateShard(newShard);
                }
                finally
                {
                    _shardCreationLock.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
            DurableJobsInstruments.OnScheduleJobCallCanceled(_timeProvider.GetElapsedTime(startTimestamp));
            throw;
        }
        catch (Exception ex)
        {
            DurableJobsInstruments.OnScheduleJobCallFailed(_timeProvider.GetElapsedTime(startTimestamp));
            DurableJobsDiagnostics.SetError(activity, ex);
            throw;
        }
    }

    private static ScheduleJobRequest EnsureScheduleRequestHasTraceContext(ScheduleJobRequest request, Activity? scheduleActivity)
    {
        if (!string.IsNullOrEmpty(request.TraceParent))
        {
            return request;
        }

        var source = scheduleActivity ?? Activity.Current;
        var (traceParent, traceState) = DurableJobsDiagnostics.CaptureTraceContext(source);
        if (traceParent is null)
        {
            return request;
        }

        return new ScheduleJobRequest
        {
            Target = request.Target,
            JobName = request.JobName,
            DueTime = request.DueTime,
            Metadata = request.Metadata,
            TraceParent = traceParent,
            TraceState = traceState,
        };
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

        _startTimestamp = _timeProvider.GetTimestamp();

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
        var startTimestamp = _timeProvider.GetTimestamp();
        try
        {
            LogCancellingJob(_logger, job.Id, job.Name, job.ShardId);

            if (_shardCache.TryGetValue(job.ShardId, out var shard))
            {
                if (!await _shardManager.IsShardOwnedByLocalSiloAsync(job.ShardId, cancellationToken))
                {
                    LogJobCancellationFailed(_logger, job.Id, job.Name, job.ShardId);
                    DurableJobsInstruments.OnCancelJobCall(_timeProvider.GetElapsedTime(startTimestamp), canceledJob: false);
                    return false;
                }

                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
                var wasRemoved = await shard.RemoveJobAsync(job.Id, linkedCts.Token);
                LogJobCancelled(_logger, job.Id, job.Name, job.ShardId);
                if (wasRemoved)
                {
                    DurableJobsInstruments.OnJobCanceled();
                }

                DurableJobsInstruments.OnCancelJobCall(_timeProvider.GetElapsedTime(startTimestamp), wasRemoved);
                return wasRemoved;
            }

            var owner = await _shardManager.GetShardOwnerAsync(job.ShardId, cancellationToken);
            if (owner is null || owner.Equals(Silo))
            {
                LogJobCancellationFailed(_logger, job.Id, job.Name, job.ShardId);
                DurableJobsInstruments.OnCancelJobCall(_timeProvider.GetElapsedTime(startTimestamp), canceledJob: false);
                return false;
            }

            var remote = _grainFactory.GetSystemTarget<ILocalDurableJobManagerSystemTarget>(JobManagerGrainType, owner);
            var routed = await remote.TryCancelDurableJobAsync(job, cancellationToken);
            if (!routed)
            {
                LogJobCancellationFailed(_logger, job.Id, job.Name, job.ShardId);
            }

            DurableJobsInstruments.OnCancelJobCall(_timeProvider.GetElapsedTime(startTimestamp), routed);
            return routed;
        }
        catch (OperationCanceledException)
        {
            DurableJobsInstruments.OnCancelJobCallCanceled(_timeProvider.GetElapsedTime(startTimestamp));
            throw;
        }
        catch
        {
            DurableJobsInstruments.OnCancelJobCallFailed(_timeProvider.GetElapsedTime(startTimestamp));
            throw;
        }
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

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10), _timeProvider);

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

                await ProcessShardCheckCycleAsync(_cts.Token);
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

    internal async Task ProcessShardCheckCycleAsync(CancellationToken cancellationToken)
    {
        LogCheckingPendingShards(_logger);

        // Clean up old writable shards that have passed their time window.
        var now = _timeProvider.GetUtcNow();
        foreach (var key in _writeableShards.Keys.ToArray())
        {
            var shardEndTime = key.StartTime.Add(_options.ShardDuration);
            if (shardEndTime < now && _writeableShards.TryRemove(key, out var expiredShard))
            {
                _writeableShardKeys.TryRemove(expiredShard.Id, out _);
                try
                {
                    await expiredShard.MarkAsCompleteAsync(cancellationToken);
                }
                catch (ObjectDisposedException ex)
                {
                    LogExpiredWritableShardAlreadyDisposed(_logger, ex, expiredShard.Id, key.StartTime, key.Stripe);
                }
            }
        }

        // Compute the slow-start budget for this cycle
        var budget = ComputeClaimBudget();

        // Query ShardManager for assigned shards (source of truth)
        var shards = await _shardManager.AssignJobShardsAsync(now.AddHours(1), budget, cancellationToken);

        // Count newly claimed shards (those not already in our cache)
        var newClaimsThisCycle = 0;
        if (shards.Count > 0)
        {
            LogAssignedShards(_logger, shards.Count);
            foreach (var shard in shards)
            {
                if (_shardCache.TryAdd(shard.Id, shard))
                {
                    newClaimsThisCycle++;
                }

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

        if (newClaimsThisCycle > 0)
        {
            _totalClaimedShards += newClaimsThisCycle;
            LogOrphanedShardsClaimed(_logger, newClaimsThisCycle, _totalClaimedShards);
        }
    }

    /// <summary>
    /// Computes the maximum number of orphaned shards this silo may claim in the current check cycle.
    /// Returns <see cref="int.MaxValue"/> when unlimited (ramp-up complete or disabled).
    /// Returns <c>0</c> when the silo is overloaded, pausing all new claims.
    /// </summary>
    private int ComputeClaimBudget()
    {
        // If overloaded, claim nothing new regardless of ramp-up phase
        if (_overloadDetector.IsOverloaded)
        {
            LogOverloadPausingShardClaims(_logger);
            return 0;
        }

        var elapsed = _timeProvider.GetElapsedTime(_startTimestamp);
        var budget = ComputeClaimBudget(
            _options.ShardClaimRampUpDuration,
            _options.ShardClaimInitialBudget,
            _options.ShardClaimMaxBudget,
            elapsed,
            _totalClaimedShards);

        if (budget < int.MaxValue)
        {
            LogShardClaimBudget(_logger, budget, _options.ShardClaimInitialBudget + (int)(elapsed.TotalMilliseconds / _options.ShardClaimRampUpDuration.TotalMilliseconds * (_options.ShardClaimMaxBudget - _options.ShardClaimInitialBudget)), _totalClaimedShards, elapsed, _options.ShardClaimRampUpDuration);
        }

        return budget;
    }

    /// <summary>
    /// Pure computation of shard-claim budget based on ramp-up parameters and elapsed time.
    /// Returns <see cref="int.MaxValue"/> when unlimited (ramp-up complete or disabled).
    /// </summary>
    internal static int ComputeClaimBudget(
        TimeSpan rampUpDuration,
        int initialBudget,
        int maxBudget,
        TimeSpan elapsed,
        int totalClaimedShards)
    {
        // Shard-claim ramp-up disabled
        if (rampUpDuration <= TimeSpan.Zero)
        {
            return int.MaxValue;
        }

        // After ramp-up period, no limit
        if (elapsed >= rampUpDuration)
        {
            return int.MaxValue;
        }

        // Linear interpolation: initialBudget at t=0, maxBudget at t=rampUpDuration
        var progress = elapsed.TotalMilliseconds / rampUpDuration.TotalMilliseconds;
        var totalBudget = initialBudget + (int)(progress * (maxBudget - initialBudget));
        var remaining = totalBudget - totalClaimedShards;
        return Math.Max(0, remaining);
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
            using var _ = new ExecutionContextSuppressor();
            _runningShards[shard.Id] = this.RunOrQueueTask(() => RunShardWithCleanupAsync(shard));
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
            TryRemoveWritableShard(shard);
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
        return _timeProvider.GetUtcNow() >= activationTime;
    }

    private bool IsWritableShard(WritableShardKey shardKey, IJobShard shard)
        => _writeableShards.TryGetValue(shardKey, out var existingShard) && ReferenceEquals(existingShard, shard);

    private bool TryRemoveWritableShard(WritableShardKey shardKey, IJobShard shard)
    {
        var entry = new KeyValuePair<WritableShardKey, IJobShard>(shardKey, shard);
        var removed = ((ICollection<KeyValuePair<WritableShardKey, IJobShard>>)_writeableShards).Remove(entry);
        if (removed)
        {
            var keyEntry = new KeyValuePair<string, WritableShardKey>(shard.Id, shardKey);
            ((ICollection<KeyValuePair<string, WritableShardKey>>)_writeableShardKeys).Remove(keyEntry);
        }

        return removed;
    }

    private bool TryRemoveWritableShard(IJobShard shard)
    {
        if (_writeableShardKeys.TryGetValue(shard.Id, out var shardKey))
        {
            return TryRemoveWritableShard(shardKey, shard);
        }

        return false;
    }

    internal sealed class TestAccessor(LocalDurableJobManager manager)
    {
        public Task ProcessShardCheckCycleAsync(CancellationToken cancellationToken) => manager.ProcessShardCheckCycleAsync(cancellationToken);

        public void AddWritableShard(DateTimeOffset shardKey, IJobShard shard, int stripe = 0)
        {
            var key = new WritableShardKey(shardKey, stripe);
            manager._writeableShards[key] = shard;
            manager._writeableShardKeys[shard.Id] = key;
            manager._shardCache.TryAdd(shard.Id, shard);
        }

        public bool HasWritableShard(DateTimeOffset shardKey, int stripe = 0) => manager._writeableShards.ContainsKey(new WritableShardKey(shardKey, stripe));

        public bool TryGetWritableShard(DateTimeOffset shardKey, out IJobShard? shard, int stripe = 0) => manager._writeableShards.TryGetValue(new WritableShardKey(shardKey, stripe), out shard);

        public int GetWritableShardStripe(ScheduleJobRequest request) => manager.GetWritableShardKey(request).Stripe;

        public int WritableShardCount => manager._writeableShards.Count;

        public void TryActivateShard(IJobShard shard) => manager.TryActivateShard(shard);

        public bool TryGetRunningShardTask(string shardId, out Task? task) => manager._runningShards.TryGetValue(shardId, out task);

        public bool HasCachedShard(string shardId) => manager._shardCache.ContainsKey(shardId);
    }

    private WritableShardKey GetWritableShardKey(ScheduleJobRequest request)
        => new(GetShardStartTime(request.DueTime), GetShardStripe());

    private IDictionary<string, string> CreateShardMetadata(WritableShardKey shardKey)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (_options.ShardStripeCount > 1)
        {
            result[ShardStripeMetadataKey] = shardKey.Stripe.ToString(CultureInfo.InvariantCulture);
        }

        return result;
    }

    private DateTimeOffset GetShardStartTime(DateTimeOffset scheduledTime)
    {
        var shardDurationTicks = _options.ShardDuration.Ticks;
        var epochTicks = scheduledTime.UtcTicks;
        var bucketTicks = (epochTicks / shardDurationTicks) * shardDurationTicks;
        return new DateTimeOffset(bucketTicks, TimeSpan.Zero);
    }

    private int GetShardStripe()
    {
        if (_options.ShardStripeCount <= 1)
        {
            return 0;
        }

        // Round-robin assignment. Stripe selection is a write-side fan-out knob only:
        // the persisted job location is the shard id, not (StartTime, Stripe), so consistency
        // across calls/silos is not required and round-robin distributes evenly under any input skew.
        var next = (uint)Interlocked.Increment(ref _stripeCounter);
        return (int)(next % (uint)_options.ShardStripeCount);
    }

    private readonly record struct WritableShardKey(DateTimeOffset StartTime, int Stripe);
}
