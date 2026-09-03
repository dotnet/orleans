using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Streams;

namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// Coordinates a recoverable stream partition pipeline comprising a partition source, pooled cache, and durable checkpoint.
    /// </summary>
    /// <typeparam name="TQueueMessage">The source record type.</typeparam>
    public sealed class RecoverableStreamReceiver<TQueueMessage> : IQueueAdapterReceiver, IQueueCache
    {
        private readonly IRecoverableStreamSource<TQueueMessage> _source;
        private readonly IRecoverableStreamDataAdapter<TQueueMessage> _dataAdapter;
        private readonly IRecoverableStreamQueueCache<TQueueMessage> _cache;
        private readonly IStreamQueueCheckpointer<string> _checkpointer;
        private readonly bool _startFromNow;
        private readonly object _lifecycleLock = new();
        private readonly CancellationTokenSource _lifecycleCancellation = new();
        private Task? _initializeTask;
        private CancellationToken _initializeTaskOwnerToken;
        private int _running;
        private int _shutdown;

        /// <summary>
        /// Initializes a new instance of the <see cref="RecoverableStreamReceiver{TQueueMessage}"/> class.
        /// </summary>
        public RecoverableStreamReceiver(
            IRecoverableStreamSource<TQueueMessage> source,
            IRecoverableStreamDataAdapter<TQueueMessage> dataAdapter,
            RecoverableStreamQueueCache<TQueueMessage> cache,
            IStreamQueueCheckpointer<string> checkpointer,
            bool startFromNow)
            : this(source, dataAdapter, (IRecoverableStreamQueueCache<TQueueMessage>)cache, checkpointer, startFromNow)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="RecoverableStreamReceiver{TQueueMessage}"/> class.
        /// </summary>
        public RecoverableStreamReceiver(
            IRecoverableStreamSource<TQueueMessage> source,
            IRecoverableStreamDataAdapter<TQueueMessage> dataAdapter,
            IRecoverableStreamQueueCache<TQueueMessage> cache,
            IStreamQueueCheckpointer<string> checkpointer,
            bool startFromNow)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _dataAdapter = dataAdapter ?? throw new ArgumentNullException(nameof(dataAdapter));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _checkpointer = checkpointer ?? throw new ArgumentNullException(nameof(checkpointer));
            _startFromNow = startFromNow;
        }

        /// <inheritdoc />
        public async Task Initialize(TimeSpan timeout)
        {
            using var cancellation = timeout == Timeout.InfiniteTimeSpan
                ? null
                : new CancellationTokenSource(timeout);
            var cancellationToken = cancellation?.Token ?? CancellationToken.None;
            await EnsureInitialized(cancellationToken);
        }

        /// <summary>
        /// Initializes the receiver.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        public Task Initialize(CancellationToken cancellationToken)
            => EnsureInitialized(cancellationToken);

        private async Task EnsureInitialized(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (true)
            {
                Task initializeTask;
                CancellationToken initializeTaskOwnerToken;
                lock (_lifecycleLock)
                {
                    if (Volatile.Read(ref _running) != 0 || Volatile.Read(ref _shutdown) != 0)
                    {
                        return;
                    }

                    if (_initializeTask is null || _initializeTask.IsCompleted)
                    {
                        _initializeTaskOwnerToken = cancellationToken;
                        _initializeTask = InitializeCore(cancellationToken);
                    }

                    initializeTask = _initializeTask;
                    initializeTaskOwnerToken = _initializeTaskOwnerToken;
                }

                try
                {
                    await initializeTask.WaitAsync(cancellationToken);
                    return;
                }
                catch (OperationCanceledException)
                    when (!cancellationToken.IsCancellationRequested
                        && Volatile.Read(ref _shutdown) == 0
                        && initializeTaskOwnerToken.IsCancellationRequested
                        && initializeTask.IsCanceled)
                {
                    // The caller which started this shared initialization canceled it. Once that
                    // task has settled, loop and create a fresh attempt for this still-active caller.
                }
            }
        }

        private async Task InitializeCore(CancellationToken initializationToken)
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                _lifecycleCancellation.Token,
                initializationToken);
            var lifecycleToken = cancellation.Token;
            var checkpoint = await _checkpointer.Load(lifecycleToken);
            await _source.Initialize(
                new RecoverableStreamStartPosition(
                    _checkpointer.CheckpointExists ? checkpoint : null,
                    _startFromNow),
                lifecycleToken);
            if (Volatile.Read(ref _shutdown) != 0)
            {
                return;
            }

            Volatile.Write(ref _running, 1);
        }

        /// <inheritdoc />
        [Obsolete("Use the overload which accepts a CancellationToken.")]
        public Task<IList<IBatchContainer>> GetQueueMessagesAsync(int maxCount)
            => GetQueueMessagesAsync(maxCount, CancellationToken.None);

        /// <inheritdoc />
        public async Task<IList<IBatchContainer>> GetQueueMessagesAsync(
            int maxCount,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _shutdown) != 0 || maxCount <= 0)
            {
                return [];
            }

            await EnsureInitialized(cancellationToken);
            if (Volatile.Read(ref _shutdown) != 0)
            {
                return [];
            }

            var messages = await _source.Read(maxCount, cancellationToken);
            if (messages.Count == 0)
            {
                return [];
            }

            IReadOnlyList<StreamPosition> positions;
            try
            {
                positions = _cache.Add(messages, DateTime.UtcNow);
                _source.MessagesAdded(messages);
            }
            catch
            {
                _source.MessagesAddFailed(messages);
                throw;
            }

            var result = new List<IBatchContainer>(positions.Count);
            foreach (var position in positions)
            {
                result.Add(new StreamActivityNotificationBatch(position));
            }

            return result;
        }

        /// <inheritdoc />
        [Obsolete("Use the overload which accepts a CancellationToken.")]
        public Task MessagesDeliveredAsync(IList<IBatchContainer> messages)
            => Task.CompletedTask;

        /// <inheritdoc />
        public Task MessagesDeliveredAsync(IList<IBatchContainer> messages, CancellationToken cancellationToken)
            => cancellationToken.IsCancellationRequested
                ? Task.FromCanceled(cancellationToken)
                : Task.CompletedTask;

        /// <inheritdoc />
        public async Task Shutdown(TimeSpan timeout)
        {
            if (Interlocked.Exchange(ref _shutdown, 1) != 0)
            {
                return;
            }

            Volatile.Write(ref _running, 0);
            _lifecycleCancellation.Cancel();
            using var cancellation = timeout == Timeout.InfiniteTimeSpan
                ? null
                : new CancellationTokenSource(timeout);
            var cancellationToken = cancellation?.Token ?? CancellationToken.None;
            List<Exception>? exceptions = null;
            Task? initializeTask;
            lock (_lifecycleLock)
            {
                initializeTask = _initializeTask;
            }

            if (initializeTask is not null)
            {
                try
                {
                    await initializeTask.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                    when (_lifecycleCancellation.IsCancellationRequested
                        && !cancellationToken.IsCancellationRequested)
                {
                }
                catch (Exception exception)
                {
                    (exceptions ??= []).Add(exception);
                }
            }

            try
            {
                await _checkpointer.FlushAsync(cancellationToken);
            }
            catch (Exception exception)
            {
                (exceptions ??= []).Add(exception);
            }

            try
            {
                await _source.Shutdown(cancellationToken);
            }
            catch (Exception exception)
            {
                (exceptions ??= []).Add(exception);
            }

            try
            {
                _cache.Dispose();
            }
            catch (Exception exception)
            {
                (exceptions ??= []).Add(exception);
            }

            if (exceptions is [var singleException])
            {
                ExceptionDispatchInfo.Capture(singleException).Throw();
            }

            if (exceptions is { Count: > 1 })
            {
                throw new AggregateException(exceptions);
            }
        }

        /// <inheritdoc />
        public int GetMaxAddCount() => _cache.GetMaxAddCount();

        /// <inheritdoc />
        public void AddToCache(IList<IBatchContainer> messages)
        {
        }

        /// <inheritdoc />
        public bool TryPurgeFromCache([MaybeNullWhen(false)] out IList<IBatchContainer> purgedItems)
            => _cache.TryPurgeFromCache(out purgedItems);

        /// <inheritdoc />
        public IQueueCacheCursor GetCacheCursor(StreamId streamId, StreamSequenceToken? token)
            => _cache.GetCacheCursor(streamId, token);

        /// <inheritdoc />
        public IQueueCacheCursor GetCacheCursorAtPosition(
            StreamId streamId,
            StreamSubscriptionStartPosition startPosition)
            => _cache.GetCacheCursorAtPosition(streamId, startPosition);

        /// <inheritdoc />
        public bool IsUnderPressure() => _cache.IsUnderPressure();

        /// <inheritdoc />
        public void UpdateDeliveryProgress(StreamSequenceToken? earliestSubscriptionToken, DateTime utcNow)
        {
            if (Volatile.Read(ref _shutdown) != 0)
            {
                return;
            }

            var progressToken = earliestSubscriptionToken;
            string? offset = null;
            if (progressToken is null)
            {
                _ = _cache.TryGetNewestPosition(out progressToken, out offset);
            }

            _cache.UpdateDeliveryProgress(progressToken, utcNow);
            if (progressToken is not null
                && (offset is not null || _dataAdapter.TryGetOffset(progressToken, out offset)))
            {
                _checkpointer.Update(offset, utcNow, CancellationToken.None);
            }
        }

        private sealed class StreamActivityNotificationBatch(StreamPosition position) : IBatchContainer
        {
            public StreamId StreamId => position.StreamId;

            public StreamSequenceToken SequenceToken => position.SequenceToken;

            public IEnumerable<Tuple<T, StreamSequenceToken>> GetEvents<T>() => throw new NotSupportedException();

            public bool ImportRequestContext() => throw new NotSupportedException();
        }
    }
}
