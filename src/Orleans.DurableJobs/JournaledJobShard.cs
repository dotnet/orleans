using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Orleans.Diagnostics;
using Orleans.Journaling;

namespace Orleans.DurableJobs;

/// <summary>
/// Journaled implementation of <see cref="IJobShard"/> that stores shard state in Orleans journaling storage.
/// </summary>
internal sealed class JournaledJobShard : IJobShard
{
    private readonly JournaledJobShardState _state;
    private readonly IJournaledStateManager _stateManager;
    private readonly JournaledJobShardManager _shardManager;
    private readonly DurableJobsInstruments _durableJobsInstruments;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _batchLingerDelay;
    private readonly int _maxBatchOperationCount;
    private readonly int _maxBatchSizeBytes;
    private readonly Channel<PendingOperation> _pendingOperations;
    private readonly CancellationTokenSource _shutdownCancellation = new();
    private readonly Task _operationProcessor;
    private int _admittedJobCount;
    private int _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="JournaledJobShard"/> class.
    /// </summary>
    /// <param name="shardId">The unique identifier for this job shard.</param>
    /// <param name="startTime">The start time of the time range managed by this shard.</param>
    /// <param name="endTime">The end time of the time range managed by this shard.</param>
    /// <param name="metadata">Optional metadata associated with this job shard.</param>
    /// <param name="isClosed">A value indicating whether this shard is closed to new jobs.</param>
    /// <param name="state">The journaled shard state.</param>
    /// <param name="stateManager">The manager used to persist journaled state.</param>
    /// <param name="shardManager">The shard manager that owns this shard.</param>
    /// <param name="timeProvider">The time provider used for batch linger delays. Defaults to <see cref="TimeProvider.System"/>.</param>
    /// <param name="batchLingerDelay">
    /// Optional duration the operation processor waits for additional mutations to join the batch
    /// after the first one arrives. Use <see cref="TimeSpan.Zero"/> (the default) to disable linger.
    /// </param>
    /// <param name="durableJobsInstruments">The durable jobs metrics instruments.</param>
    public JournaledJobShard(
        JobShardId shardId,
        DateTimeOffset startTime,
        DateTimeOffset endTime,
        IReadOnlyDictionary<string, string>? metadata,
        bool isClosed,
        JournaledJobShardState state,
        IJournaledStateManager stateManager,
        JournaledJobShardManager shardManager,
        TimeProvider? timeProvider = null,
        TimeSpan batchLingerDelay = default,
        DurableJobsInstruments? durableJobsInstruments = null,
        int maxBatchOperationCount = 1_024,
        int maxBatchSizeBytes = 1024 * 1024,
        int maxPendingOperationCount = 4_096)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(stateManager);
        ArgumentNullException.ThrowIfNull(shardManager);
        if (batchLingerDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(batchLingerDelay), batchLingerDelay, "Batch linger delay must be non-negative.");
        }
        if (maxBatchOperationCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBatchOperationCount));
        }
        if (maxBatchSizeBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBatchSizeBytes));
        }
        if (maxPendingOperationCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPendingOperationCount));
        }

        Id = shardId.Value;
        StartTime = startTime;
        EndTime = endTime;
        Metadata = metadata is { Count: > 0 } ? new Dictionary<string, string>(metadata, StringComparer.Ordinal) : null;
        _state = state;
        _stateManager = stateManager;
        _shardManager = shardManager;
        _durableJobsInstruments = durableJobsInstruments ?? DurableJobsInstruments.CreateForDirectConstruction();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _batchLingerDelay = batchLingerDelay;
        _maxBatchOperationCount = maxBatchOperationCount;
        _maxBatchSizeBytes = maxBatchSizeBytes;
        _admittedJobCount = state.Count;
        _pendingOperations = Channel.CreateBounded<PendingOperation>(
            new BoundedChannelOptions(maxPendingOperationCount)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false,
            });

        if (isClosed)
        {
            _state.MarkAsComplete();
        }

        _operationProcessor = Task.Run(ProcessOperationsAsync);
    }

    /// <inheritdoc/>
    public string Id { get; }

    /// <inheritdoc/>
    public DateTimeOffset StartTime { get; }

    /// <inheritdoc/>
    public DateTimeOffset EndTime { get; }

    /// <inheritdoc/>
    public IDictionary<string, string>? Metadata { get; }

    /// <inheritdoc/>
    public bool IsAddingCompleted => _state.IsAddingCompleted;

    /// <summary>
    /// Gets the backing journal identifier for this shard.
    /// </summary>
    internal JournalId StorageId => JobShardId.Parse(Id).ToJournalId();

    /// <inheritdoc/>
    public IAsyncEnumerable<IJobRunContext> ConsumeDurableJobsAsync() => _state.ConsumeDurableJobsAsync();

    /// <inheritdoc/>
    public ValueTask<int> GetJobCountAsync() => ValueTask.FromResult(_state.Count);

    /// <inheritdoc/>
    public async Task MarkAsCompleteAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        var operation = new MarkAsCompleteOperation(cancellationToken);
        try
        {
            await EnqueueOperationAsync(operation).ConfigureAwait(false);
            await operation.Task.ConfigureAwait(false);
        }
        finally
        {
            operation.Dispose();
        }
    }

    /// <inheritdoc/>
    public async Task<DurableJobMutationResult> TryStartAttemptAsync(
        IJobRunContext jobContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(jobContext);
        ThrowIfDisposed();

        var operation = new StartAttemptOperation(jobContext.Job.Id, cancellationToken);
        try
        {
            EnqueueOperation(operation);
            return await operation.Task.ConfigureAwait(false);
        }
        finally
        {
            operation.Dispose();
        }
    }

    /// <inheritdoc/>
    public async Task<DurableJobMutationResult> RemoveJobAsync(string jobId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ThrowIfDisposed();

        var operation = new RemoveJobOperation(jobId, cancellationToken);
        try
        {
            await EnqueueOperationAsync(operation).ConfigureAwait(false);
            var removed = await operation.Task.ConfigureAwait(false);
            if (removed)
            {
                ReleaseJobSlot();
            }

            return removed;
        }
        finally
        {
            operation.Dispose();
        }
    }

    /// <inheritdoc/>
    public async Task<DurableJobMutationResult> RetryJobLaterAsync(
        IJobRunContext jobContext,
        DateTimeOffset newDueTime,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(jobContext);
        ThrowIfDisposed();

        var operation = new RetryJobLaterOperation(jobContext, newDueTime, resetDequeueCount: false, cancellationToken);
        try
        {
            EnqueueOperation(operation);
            return await operation.Task.ConfigureAwait(false);
        }
        finally
        {
            operation.Dispose();
        }
    }

    /// <inheritdoc/>
    public async Task<DurableJobMutationResult> RescheduleJobAsync(
        IJobRunContext jobContext,
        DateTimeOffset newDueTime,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(jobContext);
        ThrowIfDisposed();

        var operation = new RetryJobLaterOperation(
            jobContext,
            newDueTime,
            resetDequeueCount: true,
            cancellationToken);
        try
        {
            EnqueueOperation(operation);
            return await operation.Task.ConfigureAwait(false);
        }
        finally
        {
            operation.Dispose();
        }
    }

    /// <inheritdoc/>
    public async Task<DurableJob?> TryScheduleJobAsync(ScheduleJobRequest request, CancellationToken cancellationToken)
    {
        request.Validate();
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (_state.IsAddingCompleted || !TryReserveJobSlot())
        {
            return null;
        }

        var operation = new ScheduleJobOperation(request, cancellationToken);
        try
        {
            await EnqueueOperationAsync(operation).ConfigureAwait(false);
            var job = await operation.Task.ConfigureAwait(false);
            if (job is null)
            {
                ReleaseJobSlot();
            }

            return job;
        }
        catch
        {
            ReleaseJobSlot();
            throw;
        }
        finally
        {
            operation.Dispose();
        }
    }

    /// <summary>
    /// Deletes this shard's journaled state.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    internal async ValueTask DeleteStateAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        var operation = new DeleteStateOperation(cancellationToken);
        try
        {
            await EnqueueOperationAsync(operation).ConfigureAwait(false);
            await operation.Task.ConfigureAwait(false);
        }
        finally
        {
            operation.Dispose();
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _shutdownCancellation.Cancel();
            _pendingOperations.Writer.TryComplete();
            await _operationProcessor.ConfigureAwait(false);
            await _stateManager.DisposeAsync();
        }
        finally
        {
            _shutdownCancellation.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed != 0, this);

    private bool TryReserveJobSlot()
    {
        while (true)
        {
            var current = Volatile.Read(ref _admittedJobCount);
            if (current >= _state.MaxJobCount)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _admittedJobCount, current + 1, current) == current)
            {
                return true;
            }
        }
    }

    private void ReleaseJobSlot()
    {
        var remaining = Interlocked.Decrement(ref _admittedJobCount);
        Debug.Assert(remaining >= 0);
    }

    private ValueTask EnqueueOperationAsync(PendingOperation operation)
    {
        operation.CancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        if (_pendingOperations.Writer.TryWrite(operation))
        {
            return ValueTask.CompletedTask;
        }

        return WaitToEnqueueOperationAsync(operation);
    }

    private async ValueTask WaitToEnqueueOperationAsync(PendingOperation operation)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            operation.CancellationToken,
            _shutdownCancellation.Token);
        try
        {
            await _pendingOperations.Writer.WriteAsync(operation, linkedCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdownCancellation.IsCancellationRequested)
        {
            throw new OperationCanceledException(_shutdownCancellation.Token);
        }
        catch (ChannelClosedException) when (_shutdownCancellation.IsCancellationRequested)
        {
            throw new OperationCanceledException(_shutdownCancellation.Token);
        }
        catch (ChannelClosedException)
        {
            ThrowIfDisposed();
            throw new InvalidOperationException("The durable job shard operation queue is no longer accepting operations.");
        }
    }

    private async Task ProcessOperationsAsync()
    {
        var batch = new List<PendingMutationOperation>(Math.Min(_maxBatchOperationCount, 1_024));
        try
        {
            while (await _pendingOperations.Reader.WaitToReadAsync(_shutdownCancellation.Token).ConfigureAwait(false))
            {
                if (!TryDequeueOperation(out var operation) || operation is null)
                {
                    continue;
                }

                if (operation is PendingMutationOperation mutation)
                {
                    batch.Add(mutation);
                    var estimatedSizeBytes = (long)mutation.EstimatedSizeBytes;
                    DequeueConsecutiveMutations(batch, ref estimatedSizeBytes);
                    if (_batchLingerDelay > TimeSpan.Zero)
                    {
                        await LingerForMoreMutationsAsync(batch).ConfigureAwait(false);
                        DequeueConsecutiveMutations(batch, ref estimatedSizeBytes);
                    }
                    _durableJobsInstruments.OnShardPendingDepth(batch.Count);
                    await ProcessMutationBatchAsync(batch).ConfigureAwait(false);
                    batch.Clear();
                }
                else
                {
                    await ProcessBarrierOperationAsync(operation).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (_shutdownCancellation.IsCancellationRequested)
        {
        }
        finally
        {
            CancelOperations(batch);
            CancelQueuedOperations();
        }
    }

    private async Task LingerForMoreMutationsAsync(List<PendingMutationOperation> batch)
    {
        try
        {
            await Task.Delay(_batchLingerDelay, _timeProvider, _shutdownCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdownCancellation.IsCancellationRequested)
        {
            return;
        }
    }

    private bool TryDequeueOperation(out PendingOperation? operation)
        => _pendingOperations.Reader.TryRead(out operation);

    private void DequeueConsecutiveMutations(List<PendingMutationOperation> batch, ref long estimatedSizeBytes)
    {
        while (batch.Count < _maxBatchOperationCount
            && _pendingOperations.Reader.TryPeek(out var operation)
            && operation is PendingMutationOperation mutation
            && (batch.Count == 0 || estimatedSizeBytes + mutation.EstimatedSizeBytes <= _maxBatchSizeBytes))
        {
            var removed = _pendingOperations.Reader.TryRead(out var dequeued);
            Debug.Assert(removed && ReferenceEquals(dequeued, mutation));
            batch.Add(mutation);
            estimatedSizeBytes += mutation.EstimatedSizeBytes;
        }
    }

    private void CancelQueuedOperations()
    {
        while (_pendingOperations.Reader.TryRead(out var operation))
        {
            operation.TryCancel(_shutdownCancellation.Token);
        }
    }

    private void CancelOperations(List<PendingMutationOperation> operations)
    {
        foreach (var operation in operations)
        {
            operation.TryCancel(_shutdownCancellation.Token);
        }

        operations.Clear();
    }

    private async Task ProcessMutationBatchAsync(List<PendingMutationOperation> operations)
    {
        var startedOperations = new List<PendingMutationOperation>(operations.Count);
        var appliedOperations = new List<PendingMutationOperation>(operations.Count);
        var operationsAwaitingWrite = new List<PendingMutationOperation>(operations.Count);

        try
        {
            var startedCount = 0;
            for (var index = 0; index < operations.Count; index++)
            {
                var operation = operations[index];
                if (!operation.TryStart())
                {
                    continue;
                }

                if (operation.TryCompleteWithoutOwnership(this))
                {
                    continue;
                }

                operations[startedCount++] = operation;
            }

            if (startedCount < operations.Count)
            {
                operations.RemoveRange(startedCount, operations.Count - startedCount);
            }

            if (operations.Count == 0)
            {
                return;
            }

            var ownershipStartTimestamp = Stopwatch.GetTimestamp();
            bool isOwned;
            try
            {
                isOwned = await _shardManager.IsShardOwnedByLocalSiloAsync(Id, _shutdownCancellation.Token).ConfigureAwait(false);
            }
            finally
            {
                _durableJobsInstruments.OnOwnershipCheck(Stopwatch.GetElapsedTime(ownershipStartTimestamp));
            }

            if (!isOwned)
            {
                foreach (var operation in operations)
                {
                    operation.CompleteNotOwned();
                }

                return;
            }

            var pendingBytesBeforeApply = _stateManager.PendingWriteByteCount;

            var appliedCount = 0;
            for (var index = 0; index < operations.Count; index++)
            {
                var operation = operations[index];
                try
                {
                    var hasPendingWrite = appliedOperations.Count > 0;
                    if (operation.Apply(this, hasPendingWrite))
                    {
                        appliedOperations.Add(operation);
                        operationsAwaitingWrite.Add(operation);
                    }
                    else if (hasPendingWrite && !operation.Completion.IsCompleted)
                    {
                        operationsAwaitingWrite.Add(operation);
                    }
                }
                catch (Exception exception)
                {
                    operation.TrySetException(exception);
                }
            }

            if (appliedCount < operations.Count)
            {
                operations.RemoveRange(appliedCount, operations.Count - appliedCount);
            }

            if (operations.Count == 0)
            {
                return;
            }

            var pendingBytesAfterApply = _stateManager.PendingWriteByteCount;
            _durableJobsInstruments.OnShardBatch(operations.Count);
            if (pendingBytesBeforeApply >= 0 && pendingBytesAfterApply >= pendingBytesBeforeApply)
            {
                _durableJobsInstruments.OnShardBatchBytes(pendingBytesAfterApply - pendingBytesBeforeApply);
            }

            try
            {
                using var batchActivity = DurableJobsDiagnostics.StartPersistBatchActivity(operations, Id);
                await _stateManager.WriteStateAsync(_shutdownCancellation.Token).ConfigureAwait(false);
                _durableJobsInstruments.OnStorageBatchWritten(appliedOperations.Count, operationCanceled: false, error: false);
                batchActivity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
                foreach (var operation in operationsAwaitingWrite)
                {
                    operation.CompleteAfterWrite();
                }
            }
            catch (OperationCanceledException exception) when (_shutdownCancellation.IsCancellationRequested)
            {
                _durableJobsInstruments.OnStorageBatchWritten(appliedOperations.Count, operationCanceled: true, error: false);
                foreach (var operation in operationsAwaitingWrite)
                {
                    operation.TrySetCanceled(exception.CancellationToken);
                }
            }
            catch (Exception exception)
            {
                _durableJobsInstruments.OnStorageBatchWritten(appliedOperations.Count, operationCanceled: false, error: true);
                foreach (var operation in operationsAwaitingWrite)
                {
                    operation.TrySetException(exception);
                }
            }
        }
        catch (OperationCanceledException exception) when (_shutdownCancellation.IsCancellationRequested)
        {
            CompleteIncompleteOperations(operations, exception);
        }
        catch (Exception exception)
        {
            CompleteIncompleteOperations(operations, exception);
        }
    }

    private async Task ProcessBarrierOperationAsync(PendingOperation operation)
    {
        if (!operation.TryStart())
        {
            return;
        }

        try
        {
            switch (operation)
            {
                case MarkAsCompleteOperation markAsComplete:
                    if (!_state.IsAddingCompleted && await _shardManager.TryMarkShardClosedAsync(Id, _shutdownCancellation.Token).ConfigureAwait(false))
                    {
                        _state.MarkAsComplete();
                    }

                    markAsComplete.Complete();
                    break;
                case DeleteStateOperation deleteState:
                    using (var deleteActivity = DurableJobsDiagnostics.StartDeleteShardActivity(deleteState.CapturedContext, Id))
                    {
                        await _stateManager.DeleteStateAsync(_shutdownCancellation.Token).ConfigureAwait(false);
                        deleteActivity?.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
                    }

                    deleteState.Complete();
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported DurableJobs shard operation '{operation.GetType().Name}'.");
            }
        }
        catch (OperationCanceledException exception) when (_shutdownCancellation.IsCancellationRequested)
        {
            operation.TrySetCanceled(exception.CancellationToken);
        }
        catch (Exception exception)
        {
            operation.TrySetException(exception);
        }
    }

    private void CompleteIncompleteOperations(List<PendingMutationOperation> operations, Exception exception)
    {
        foreach (var operation in operations)
        {
            if (exception is OperationCanceledException cancellation && _shutdownCancellation.IsCancellationRequested)
            {
                operation.TrySetCanceled(cancellation.CancellationToken);
            }
            else
            {
                operation.TrySetException(exception);
            }
        }
    }

    private abstract class PendingOperation : IDisposable, DurableJobsDiagnostics.IHasCapturedContext
    {
        private readonly CancellationTokenRegistration _cancellationRegistration;
        private int _started;

        protected PendingOperation(CancellationToken cancellationToken)
        {
            CancellationToken = cancellationToken;
            CapturedContext = Activity.Current is { IdFormat: ActivityIdFormat.W3C } current
                ? current.Context
                : default;
            if (cancellationToken.CanBeCanceled)
            {
                _cancellationRegistration = cancellationToken.Register(static state => ((PendingOperation)state!).TryCancel(), this);
            }
        }

        public CancellationToken CancellationToken { get; }

        public ActivityContext CapturedContext { get; }

        public abstract Task Completion { get; }

        public bool TryStart()
        {
            if (CancellationToken.IsCancellationRequested)
            {
                TryCancel();
                return false;
            }

            if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            {
                return false;
            }

            return !Completion.IsCompleted;
        }

        public void TryCancel() => TryCancel(CancellationToken);

        public void TryCancel(CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _started) == 0)
            {
                TrySetCanceled(cancellationToken);
            }
        }

        public abstract void TrySetCanceled(CancellationToken cancellationToken);

        public abstract void TrySetException(Exception exception);

        public void Dispose() => _cancellationRegistration.Dispose();
    }

    private abstract class PendingOperation<TResult> : PendingOperation
    {
        private readonly TaskCompletionSource<TResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected PendingOperation(CancellationToken cancellationToken) : base(cancellationToken)
        {
        }

        public Task<TResult> Task => _completion.Task;

        public override Task Completion => _completion.Task;

        public override void TrySetCanceled(CancellationToken cancellationToken) => _completion.TrySetCanceled(cancellationToken);

        public override void TrySetException(Exception exception) => _completion.TrySetException(exception);

        protected void TrySetResult(TResult result) => _completion.TrySetResult(result);
    }

    private abstract class PendingMutationOperation : PendingOperation
    {
        protected PendingMutationOperation(CancellationToken cancellationToken) : base(cancellationToken)
        {
        }

        public virtual bool TryCompleteWithoutOwnership(JournaledJobShard shard) => false;

        public abstract int EstimatedSizeBytes { get; }

        public abstract void CompleteNotOwned();

        public abstract bool Apply(JournaledJobShard shard, bool deferCompletion);

        public abstract void CompleteAfterWrite();
    }

    private abstract class PendingMutationOperation<TResult> : PendingMutationOperation
    {
        private readonly TaskCompletionSource<TResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TResult _result = default!;

        protected PendingMutationOperation(CancellationToken cancellationToken) : base(cancellationToken)
        {
        }

        public Task<TResult> Task => _completion.Task;

        public override Task Completion => _completion.Task;

        protected abstract TResult NotOwnedResult { get; }

        protected abstract bool Apply(JournaledJobShard shard, out TResult result);

        public override void TrySetCanceled(CancellationToken cancellationToken) => _completion.TrySetCanceled(cancellationToken);

        public override void TrySetException(Exception exception) => _completion.TrySetException(exception);

        public override void CompleteNotOwned() => _completion.TrySetResult(NotOwnedResult);

        public override bool Apply(JournaledJobShard shard, bool deferCompletion)
        {
            var writeRequired = Apply(shard, out _result);
            if (!writeRequired && !deferCompletion)
            {
                _completion.TrySetResult(_result);
            }

            return writeRequired;
        }

        public override void CompleteAfterWrite() => _completion.TrySetResult(_result);

        protected void TrySetResult(TResult result) => _completion.TrySetResult(result);
    }

    private sealed class ScheduleJobOperation(ScheduleJobRequest request, CancellationToken cancellationToken)
        : PendingMutationOperation<DurableJob?>(cancellationToken)
    {
        public override int EstimatedSizeBytes { get; } = EstimateScheduleRequestSize(request);

        protected override DurableJob? NotOwnedResult => null;

        public override bool TryCompleteWithoutOwnership(JournaledJobShard shard)
        {
            if (!shard._state.IsAddingCompleted)
            {
                return false;
            }

            TrySetResult(null);
            return true;
        }

        protected override bool Apply(JournaledJobShard shard, out DurableJob? result)
        {
            result = shard._state.TryScheduleJob(request);
            return result is not null;
        }
    }

    private sealed class RemoveJobOperation(string jobId, CancellationToken cancellationToken)
        : PendingMutationOperation<DurableJobMutationResult>(cancellationToken)
    {
        public override int EstimatedSizeBytes { get; } = 128 + EstimateTextSize(jobId);

        protected override DurableJobMutationResult NotOwnedResult => DurableJobMutationResult.OwnershipLost;

        protected override bool Apply(JournaledJobShard shard, out DurableJobMutationResult result)
        {
            result = shard._state.RemoveJob(jobId)
                ? DurableJobMutationResult.Applied
                : DurableJobMutationResult.JobNotFound;
            return result == DurableJobMutationResult.Applied;
        }
    }

    private sealed class StartAttemptOperation(string jobId, CancellationToken cancellationToken)
        : PendingMutationOperation<DurableJobMutationResult>(cancellationToken)
    {
        protected override DurableJobMutationResult NotOwnedResult => DurableJobMutationResult.OwnershipLost;

        protected override bool Apply(JournaledJobShard shard, out DurableJobMutationResult result)
        {
            result = shard._state.ContainsJob(jobId)
                ? DurableJobMutationResult.Applied
                : DurableJobMutationResult.JobNotFound;
            return false;
        }
    }

    private sealed class RetryJobLaterOperation(
        IJobRunContext jobContext,
        DateTimeOffset newDueTime,
        bool resetDequeueCount,
        CancellationToken cancellationToken)
        : PendingMutationOperation<DurableJobMutationResult>(cancellationToken)
    {
        public override int EstimatedSizeBytes { get; } = 192 + EstimateTextSize(jobContext.Job.Id);

        protected override DurableJobMutationResult NotOwnedResult => DurableJobMutationResult.OwnershipLost;

        protected override bool Apply(JournaledJobShard shard, out DurableJobMutationResult result)
        {
            result = shard._state.RetryJobLater(
                jobContext.Job.Id,
                newDueTime,
                resetDequeueCount ? 0 : jobContext.DequeueCount,
                resetDequeueCount ? checked(jobContext.Job.ExecutionGeneration + 1) : null)
                ? DurableJobMutationResult.Applied
                : DurableJobMutationResult.JobNotFound;
            return result == DurableJobMutationResult.Applied;
        }
    }

    private sealed class MarkAsCompleteOperation(CancellationToken cancellationToken) : PendingOperation<bool>(cancellationToken)
    {
        public void Complete() => TrySetResult(true);
    }

    private sealed class DeleteStateOperation(CancellationToken cancellationToken) : PendingOperation<bool>(cancellationToken)
    {
        public void Complete() => TrySetResult(true);
    }

    private static int EstimateScheduleRequestSize(ScheduleJobRequest request)
    {
        long result = 512L
            + EstimateTextSize(request.JobName)
            + EstimateTextSize(request.Target.ToString())
            + EstimateTextSize(request.TraceParent)
            + EstimateTextSize(request.TraceState);

        if (request.Metadata is { } metadata)
        {
            foreach (var (key, value) in metadata)
            {
                result += EstimateTextSize(key) + EstimateTextSize(value) + 32L;
            }
        }

        return (int)Math.Min(int.MaxValue, result);
    }

    private static int EstimateTextSize(string? value)
        => string.IsNullOrEmpty(value) ? 0 : (int)Math.Min(int.MaxValue, (long)value.Length * 6);
}
