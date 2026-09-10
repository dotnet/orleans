using System;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Streams;

/// <summary>
/// Coalesces and persists stream queue checkpoints using an <see cref="IStreamCheckpointStore"/>.
/// </summary>
public sealed class StreamQueueCheckpointer : IStreamQueueCheckpointer<string>
{
    private readonly IStreamCheckpointStore _store;
    private readonly StreamQueueCheckpointerOptions _options;
    private readonly object _lock = new();

    private string _latestCheckpoint = string.Empty;
    private StreamCheckpointStoreState _persistedState = new(string.Empty, string.Empty);
    private Task _inProgressSave = Task.CompletedTask;
    private DateTime? _throttleSavesUntilUtc;
    private int _pendingResetCount;
    private long _updateGeneration;

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamQueueCheckpointer"/> class.
    /// </summary>
    /// <param name="store">The checkpoint store.</param>
    /// <param name="options">The checkpointer options.</param>
    public StreamQueueCheckpointer(IStreamCheckpointStore store, StreamQueueCheckpointerOptions options)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        if (options.PersistInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.PersistInterval,
                $"{nameof(StreamQueueCheckpointerOptions.PersistInterval)} must be greater than zero.");
        }

        _store = store;
        _options = options;
    }

    /// <inheritdoc />
    public bool CheckpointExists
    {
        get
        {
            lock (_lock)
            {
                return !string.IsNullOrEmpty(_latestCheckpoint);
            }
        }
    }

    /// <inheritdoc />
    [Obsolete("Use the overload which accepts a CancellationToken.")]
    public Task<string> Load() => Load(CancellationToken.None);

    /// <inheritdoc />
    public async Task<string> Load(CancellationToken cancellationToken)
    {
        var state = await _store.Load(cancellationToken);
        lock (_lock)
        {
            _latestCheckpoint = state.Checkpoint;
            _persistedState = state;
        }

        return state.Checkpoint;
    }

    /// <inheritdoc />
    [Obsolete("Use the overload which accepts a CancellationToken.")]
    public Task Reset() => Reset(CancellationToken.None);

    /// <inheritdoc />
    public Task Reset(CancellationToken cancellationToken)
    {
        Task resetTask;
        lock (_lock)
        {
            _pendingResetCount++;
            _throttleSavesUntilUtc = DateTime.MaxValue;
            resetTask = _inProgressSave = RunReset(
                _inProgressSave,
                _latestCheckpoint,
                _updateGeneration,
                cancellationToken);
            resetTask.Ignore();
        }

        return resetTask.WaitAsync(cancellationToken);
    }

    private async Task RunReset(
        Task inProgressSave,
        string enqueuedCheckpoint,
        long updateGeneration,
        CancellationToken cancellationToken)
    {
        try
        {
            await ResetCore(
                inProgressSave,
                enqueuedCheckpoint,
                updateGeneration,
                cancellationToken);
        }
        finally
        {
            lock (_lock)
            {
                _pendingResetCount--;
                if (_pendingResetCount == 0)
                {
                    _throttleSavesUntilUtc = null;
                }
            }
        }
    }

    /// <inheritdoc />
    [Obsolete("Use the overload which accepts a CancellationToken.")]
    public void Update(string offset, DateTime utcNow)
        => Update(offset, utcNow, CancellationToken.None);

    /// <inheritdoc />
    public void Update(string offset, DateTime utcNow, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(offset);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_lock)
        {
            if (string.Equals(_latestCheckpoint, offset, StringComparison.Ordinal))
            {
                if (string.Equals(_persistedState.Checkpoint, offset, StringComparison.Ordinal)
                    || !_inProgressSave.IsCompleted
                    || (_throttleSavesUntilUtc.HasValue && _throttleSavesUntilUtc.Value > utcNow))
                {
                    return;
                }

                _throttleSavesUntilUtc = utcNow + _options.PersistInterval;
                _inProgressSave = Save(offset, cancellationToken);
                _inProgressSave.Ignore();
                return;
            }

            if (_options.CheckpointComparer is { } comparer
                    && !string.IsNullOrEmpty(_latestCheckpoint)
                    && comparer.Compare(offset, _latestCheckpoint) <= 0)
            {
                return;
            }

            _latestCheckpoint = offset;
            _updateGeneration++;
            if (_throttleSavesUntilUtc.HasValue
                && (_throttleSavesUntilUtc.Value > utcNow || !_inProgressSave.IsCompleted))
            {
                return;
            }

            _throttleSavesUntilUtc = utcNow + _options.PersistInterval;
            _inProgressSave = Save(offset, cancellationToken);
            _inProgressSave.Ignore();
        }
    }

    /// <inheritdoc />
    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        var retryingSave = false;
        while (true)
        {
            Task inProgressSave;
            lock (_lock)
            {
                inProgressSave = _inProgressSave;
            }

            if (retryingSave)
            {
                await inProgressSave.WaitAsync(cancellationToken);
            }
            else
            {
                try
                {
                    await inProgressSave.WaitAsync(cancellationToken);
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                }

                cancellationToken.ThrowIfCancellationRequested();
            }

            lock (_lock)
            {
                if (!ReferenceEquals(inProgressSave, _inProgressSave))
                {
                    retryingSave = false;
                    continue;
                }

                if (string.Equals(_persistedState.Checkpoint, _latestCheckpoint, StringComparison.Ordinal))
                {
                    return;
                }

                _inProgressSave = Save(_latestCheckpoint, cancellationToken);
                _inProgressSave.Ignore();
                retryingSave = true;
            }
        }
    }

    private async Task Save(string checkpoint, CancellationToken cancellationToken)
    {
        string expectedVersion;
        lock (_lock)
        {
            expectedVersion = _persistedState.Version;
        }

        while (true)
        {
            var persistedState = await _store.Update(checkpoint, expectedVersion, cancellationToken);

            lock (_lock)
            {
                _persistedState = persistedState;
                if (string.Equals(persistedState.Checkpoint, checkpoint, StringComparison.Ordinal))
                {
                    return;
                }

                if (_options.CheckpointComparer is not { } comparer)
                {
                    _latestCheckpoint = persistedState.Checkpoint;
                    return;
                }

                if (Compare(comparer, _latestCheckpoint, persistedState.Checkpoint) <= 0)
                {
                    _latestCheckpoint = persistedState.Checkpoint;
                }

                if (Compare(comparer, checkpoint, persistedState.Checkpoint) <= 0)
                {
                    return;
                }

                expectedVersion = persistedState.Version;
            }
        }
    }

    private async Task ResetCore(
        Task inProgressSave,
        string enqueuedCheckpoint,
        long updateGeneration,
        CancellationToken cancellationToken)
    {
        try
        {
            await inProgressSave;
        }
        catch
        {
        }

        cancellationToken.ThrowIfCancellationRequested();

        string rollbackCheckpoint;
        string expectedVersion;
        string persistedVersion;
        lock (_lock)
        {
            persistedVersion = expectedVersion = _persistedState.Version;
            if (_updateGeneration <= updateGeneration)
            {
                rollbackCheckpoint = _latestCheckpoint;
                _latestCheckpoint = string.Empty;
            }
            else
            {
                rollbackCheckpoint = enqueuedCheckpoint;
            }
        }

        try
        {
            while (true)
            {
                var persistedState = await _store.Update(string.Empty, expectedVersion, cancellationToken);
                lock (_lock)
                {
                    _persistedState = persistedState;
                }

                if (string.IsNullOrEmpty(persistedState.Checkpoint))
                {
                    return;
                }

                expectedVersion = persistedState.Version;
            }
        }
        catch
        {
            lock (_lock)
            {
                RestoreLatestCheckpoint(rollbackCheckpoint, persistedVersion);
            }

            throw;
        }
    }

    private void RestoreLatestCheckpoint(string latestCheckpoint, string persistedVersion)
    {
        if (string.Equals(_persistedState.Version, persistedVersion, StringComparison.Ordinal))
        {
            if (_options.CheckpointComparer is { } checkpointComparer)
            {
                if (Compare(checkpointComparer, _latestCheckpoint, latestCheckpoint) < 0)
                {
                    _latestCheckpoint = latestCheckpoint;
                }
            }
            else if (string.IsNullOrEmpty(_latestCheckpoint))
            {
                _latestCheckpoint = latestCheckpoint;
            }

            return;
        }

        if (_options.CheckpointComparer is not { } comparer)
        {
            _latestCheckpoint = _persistedState.Checkpoint;
            return;
        }

        var candidate = _latestCheckpoint;
        if (Compare(comparer, candidate, latestCheckpoint) < 0)
        {
            candidate = latestCheckpoint;
        }

        if (Compare(comparer, candidate, _persistedState.Checkpoint) < 0)
        {
            candidate = _persistedState.Checkpoint;
        }

        _latestCheckpoint = candidate;
    }

    private static int Compare(IComparer<string> comparer, string left, string right)
    {
        if (string.IsNullOrEmpty(left))
        {
            return string.IsNullOrEmpty(right) ? 0 : -1;
        }

        return string.IsNullOrEmpty(right) ? 1 : comparer.Compare(left, right);
    }
}
