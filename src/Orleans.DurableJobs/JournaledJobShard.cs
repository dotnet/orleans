using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _batchLingerDelay;
    private readonly object _pendingOperationsLock = new();
    private readonly Queue<PendingOperation> _pendingOperations = new();
    private readonly SemaphoreSlim _pendingOperationSignal = new(0);
    private readonly CancellationTokenSource _shutdownCancellation = new();
    private readonly Task _operationProcessor;
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
        TimeSpan batchLingerDelay = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(stateManager);
        ArgumentNullException.ThrowIfNull(shardManager);
        if (batchLingerDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(batchLingerDelay), batchLingerDelay, "Batch linger delay must be non-negative.");
        }

        Id = shardId.Value;
        StartTime = startTime;
        EndTime = endTime;
        Metadata = metadata is { Count: > 0 } ? new Dictionary<string, string>(metadata, StringComparer.Ordinal) : null;
        _state = state;
        _stateManager = stateManager;
        _shardManager = shardManager;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _batchLingerDelay = batchLingerDelay;

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
            EnqueueOperation(operation);
            await operation.Task.ConfigureAwait(false);
        }
        finally
        {
            operation.Dispose();
        }
    }

    /// <inheritdoc/>
    public async Task<bool> RemoveJobAsync(string jobId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ThrowIfDisposed();

        var operation = new RemoveJobOperation(jobId, cancellationToken);
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
    public async Task RetryJobLaterAsync(IJobRunContext jobContext, DateTimeOffset newDueTime, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(jobContext);
        ThrowIfDisposed();

        var operation = new RetryJobLaterOperation(jobContext, newDueTime, cancellationToken);
        try
        {
            EnqueueOperation(operation);
            await operation.Task.ConfigureAwait(false);
        }
        finally
        {
            operation.Dispose();
        }
    }

    /// <inheritdoc/>
    public async Task<DurableJob?> TryScheduleJobAsync(ScheduleJobRequest request, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        var operation = new ScheduleJobOperation(request, cancellationToken);
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
            EnqueueOperation(operation);
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
            _pendingOperationSignal.Release();
            await _operationProcessor.ConfigureAwait(false);
            await _stateManager.DisposeAsync();
        }
        finally
        {
            _shutdownCancellation.Dispose();
            _pendingOperationSignal.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed != 0, this);

    private void EnqueueOperation(PendingOperation operation)
    {
        operation.CancellationToken.ThrowIfCancellationRequested();

        lock (_pendingOperationsLock)
        {
            ThrowIfDisposed();
            _pendingOperations.Enqueue(operation);
            _pendingOperationSignal.Release();
        }
    }

    private async Task ProcessOperationsAsync()
    {
        var batch = new List<PendingMutationOperation>();
        try
        {
            while (true)
            {
                await _pendingOperationSignal.WaitAsync(_shutdownCancellation.Token).ConfigureAwait(false);

                if (!TryDequeueOperation(out var operation) || operation is null)
                {
                    continue;
                }

                if (operation is PendingMutationOperation mutation)
                {
                    batch.Add(mutation);
                    DequeueConsecutiveMutations(batch);
                    if (_batchLingerDelay > TimeSpan.Zero)
                    {
                        await LingerForMoreMutationsAsync(batch).ConfigureAwait(false);
                        DequeueConsecutiveMutations(batch);
                    }
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
    {
        lock (_pendingOperationsLock)
        {
            return _pendingOperations.TryDequeue(out operation);
        }
    }

    private void DequeueConsecutiveMutations(List<PendingMutationOperation> batch)
    {
        lock (_pendingOperationsLock)
        {
            while (_pendingOperations.TryPeek(out var operation) && operation is PendingMutationOperation mutation)
            {
                _pendingOperations.Dequeue();
                batch.Add(mutation);
            }
        }
    }

    private void CancelQueuedOperations()
    {
        lock (_pendingOperationsLock)
        {
            while (_pendingOperations.TryDequeue(out var operation))
            {
                operation.TryCancel(_shutdownCancellation.Token);
            }
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

        try
        {
            foreach (var operation in operations)
            {
                if (!operation.TryStart())
                {
                    continue;
                }

                if (operation.TryCompleteWithoutOwnership(this))
                {
                    continue;
                }

                startedOperations.Add(operation);
            }

            if (startedOperations.Count == 0)
            {
                return;
            }

            if (!await _shardManager.IsShardOwnedByLocalSiloAsync(Id, _shutdownCancellation.Token).ConfigureAwait(false))
            {
                foreach (var operation in startedOperations)
                {
                    operation.CompleteNotOwned();
                }

                return;
            }

            foreach (var operation in startedOperations)
            {
                try
                {
                    if (operation.Apply(this))
                    {
                        appliedOperations.Add(operation);
                    }
                }
                catch (Exception exception)
                {
                    operation.TrySetException(exception);
                }
            }

            if (appliedOperations.Count == 0)
            {
                return;
            }

            try
            {
                await _stateManager.WriteStateAsync(_shutdownCancellation.Token).ConfigureAwait(false);
                DurableJobsInstruments.OnStorageBatchWritten(appliedOperations.Count, canceled: false, error: false);
                foreach (var operation in appliedOperations)
                {
                    operation.CompleteAfterWrite();
                }
            }
            catch (OperationCanceledException exception) when (_shutdownCancellation.IsCancellationRequested)
            {
                DurableJobsInstruments.OnStorageBatchWritten(appliedOperations.Count, canceled: true, error: false);
                foreach (var operation in appliedOperations)
                {
                    operation.TrySetCanceled(exception.CancellationToken);
                }
            }
            catch (Exception exception)
            {
                DurableJobsInstruments.OnStorageBatchWritten(appliedOperations.Count, canceled: false, error: true);
                foreach (var operation in appliedOperations)
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
                    await _stateManager.DeleteStateAsync(_shutdownCancellation.Token).ConfigureAwait(false);
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

    private abstract class PendingOperation : IDisposable
    {
        private readonly CancellationTokenRegistration _cancellationRegistration;
        private int _started;

        protected PendingOperation(CancellationToken cancellationToken)
        {
            CancellationToken = cancellationToken;
            if (cancellationToken.CanBeCanceled)
            {
                _cancellationRegistration = cancellationToken.Register(static state => ((PendingOperation)state!).TryCancel(), this);
            }
        }

        public CancellationToken CancellationToken { get; }

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

        public abstract void CompleteNotOwned();

        public abstract bool Apply(JournaledJobShard shard);

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

        public override bool Apply(JournaledJobShard shard)
        {
            var writeRequired = Apply(shard, out _result);
            if (!writeRequired)
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
        : PendingMutationOperation<bool>(cancellationToken)
    {
        protected override bool NotOwnedResult => false;

        protected override bool Apply(JournaledJobShard shard, out bool result)
        {
            result = shard._state.RemoveJob(jobId);
            return true;
        }
    }

    private sealed class RetryJobLaterOperation(IJobRunContext jobContext, DateTimeOffset newDueTime, CancellationToken cancellationToken)
        : PendingMutationOperation<bool>(cancellationToken)
    {
        protected override bool NotOwnedResult => true;

        protected override bool Apply(JournaledJobShard shard, out bool result)
        {
            shard._state.RetryJobLater(jobContext, newDueTime);
            result = true;
            return true;
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
}
