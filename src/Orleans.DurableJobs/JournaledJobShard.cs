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
    private readonly SemaphoreSlim _operationLock = new(1, 1);
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
    public JournaledJobShard(
        JobShardId shardId,
        DateTimeOffset startTime,
        DateTimeOffset endTime,
        IReadOnlyDictionary<string, string>? metadata,
        bool isClosed,
        JournaledJobShardState state,
        IJournaledStateManager stateManager,
        JournaledJobShardManager shardManager)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(stateManager);
        ArgumentNullException.ThrowIfNull(shardManager);

        Id = shardId.Value;
        StartTime = startTime;
        EndTime = endTime;
        Metadata = metadata is { Count: > 0 } ? new Dictionary<string, string>(metadata, StringComparer.Ordinal) : null;
        _state = state;
        _stateManager = stateManager;
        _shardManager = shardManager;

        if (isClosed)
        {
            _state.MarkAsComplete();
        }
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

        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            if (_state.IsAddingCompleted)
            {
                return;
            }

            if (await _shardManager.TryMarkShardClosedAsync(Id, cancellationToken))
            {
                _state.MarkAsComplete();
            }
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<bool> RemoveJobAsync(string jobId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ThrowIfDisposed();

        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            if (!await _shardManager.IsShardOwnedByLocalSiloAsync(Id, cancellationToken))
            {
                return false;
            }

            var removed = _state.RemoveJob(jobId);
            await _stateManager.WriteStateAsync(cancellationToken);
            return removed;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task RetryJobLaterAsync(IJobRunContext jobContext, DateTimeOffset newDueTime, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(jobContext);
        ThrowIfDisposed();

        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            if (!await _shardManager.IsShardOwnedByLocalSiloAsync(Id, cancellationToken))
            {
                return;
            }

            _state.RetryJobLater(jobContext, newDueTime);
            await _stateManager.WriteStateAsync(cancellationToken);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<DurableJob?> TryScheduleJobAsync(ScheduleJobRequest request, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            if (_state.IsAddingCompleted)
            {
                return null;
            }

            if (!await _shardManager.IsShardOwnedByLocalSiloAsync(Id, cancellationToken))
            {
                return null;
            }

            var job = _state.TryScheduleJob(request);
            if (job is null)
            {
                return null;
            }

            await _stateManager.WriteStateAsync(cancellationToken);
            return job;
        }
        finally
        {
            _operationLock.Release();
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

        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            await _stateManager.DeleteStateAsync(cancellationToken);
        }
        finally
        {
            _operationLock.Release();
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
            await _stateManager.DisposeAsync();
        }
        finally
        {
            _operationLock.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed != 0, this);
}
