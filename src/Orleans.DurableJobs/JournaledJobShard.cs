using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Journaling;

namespace Orleans.DurableJobs;

internal sealed class JournaledJobShard : IJobShard
{
    private readonly JournaledJobShardState _state;
    private readonly IJournaledStateManager _stateManager;
    private readonly JournaledJobShardManager _shardManager;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private int _disposed;

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

    public string Id { get; }

    public DateTimeOffset StartTime { get; }

    public DateTimeOffset EndTime { get; }

    public IDictionary<string, string>? Metadata { get; }

    public bool IsAddingCompleted => _state.IsAddingCompleted;

    internal JournalStorageId StorageId => JobShardId.Parse(Id).ToJournalStorageId();

    public IAsyncEnumerable<IJobRunContext> ConsumeDurableJobsAsync() => _state.ConsumeDurableJobsAsync();

    public ValueTask<int> GetJobCountAsync() => ValueTask.FromResult(_state.Count);

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

            _state.MarkAsComplete();
        }
        finally
        {
            _operationLock.Release();
        }
    }

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
