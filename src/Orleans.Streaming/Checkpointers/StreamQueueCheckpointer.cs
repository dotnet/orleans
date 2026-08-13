using System;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Streams
{
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
        public void Update(string offset, DateTime utcNow)
            => Update(offset, utcNow, CancellationToken.None);

        /// <inheritdoc />
        public void Update(string offset, DateTime utcNow, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(offset);
            cancellationToken.ThrowIfCancellationRequested();

            lock (_lock)
            {
                if (string.Equals(_latestCheckpoint, offset, StringComparison.Ordinal)
                    || (_options.CheckpointComparer is { } comparer
                        && !string.IsNullOrEmpty(_latestCheckpoint)
                        && comparer.Compare(offset, _latestCheckpoint) <= 0))
                {
                    return;
                }

                _latestCheckpoint = offset;
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
                        if (string.Equals(_latestCheckpoint, checkpoint, StringComparison.Ordinal))
                        {
                            _latestCheckpoint = persistedState.Checkpoint;
                        }

                        return;
                    }

                    if (comparer.Compare(_latestCheckpoint, persistedState.Checkpoint) <= 0)
                    {
                        _latestCheckpoint = persistedState.Checkpoint;
                    }

                    if (comparer.Compare(checkpoint, persistedState.Checkpoint) <= 0)
                    {
                        return;
                    }

                    expectedVersion = persistedState.Version;
                }
            }
        }
    }
}
