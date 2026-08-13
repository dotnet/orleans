using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Streams;

namespace Orleans.Providers.Streams.Common
{
    /// <summary>
    /// Coordinates a recoverable partition source, pooled cache, and durable checkpoint.
    /// </summary>
    /// <typeparam name="TQueueMessage">The source record type.</typeparam>
    public sealed class RecoverableStreamReceiver<TQueueMessage> : IQueueAdapterReceiver, IQueueCache
    {
        private readonly IRecoverableStreamSource<TQueueMessage> _source;
        private readonly IRecoverableStreamDataAdapter<TQueueMessage> _dataAdapter;
        private readonly RecoverableStreamQueueCache<TQueueMessage> _cache;
        private readonly IStreamQueueCheckpointer<string> _checkpointer;
        private readonly bool _startFromNow;
        private int _running;

        /// <summary>
        /// Initializes a new instance of the <see cref="RecoverableStreamReceiver{TQueueMessage}"/> class.
        /// </summary>
        public RecoverableStreamReceiver(
            IRecoverableStreamSource<TQueueMessage> source,
            IRecoverableStreamDataAdapter<TQueueMessage> dataAdapter,
            RecoverableStreamQueueCache<TQueueMessage> cache,
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
            var checkpoint = await _checkpointer.Load(cancellationToken);
            await _source.Initialize(
                new RecoverableStreamStartPosition(
                    _checkpointer.CheckpointExists ? checkpoint : null,
                    _startFromNow),
                cancellationToken);
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
            if (Volatile.Read(ref _running) == 0 || maxCount <= 0)
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
            if (Interlocked.Exchange(ref _running, 0) == 0)
            {
                return;
            }

            using var cancellation = timeout == Timeout.InfiniteTimeSpan
                ? null
                : new CancellationTokenSource(timeout);
            var cancellationToken = cancellation?.Token ?? CancellationToken.None;
            await _checkpointer.FlushAsync(cancellationToken);
            await _source.Shutdown(cancellationToken);
            _cache.Dispose();
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
        public bool IsUnderPressure() => _cache.IsUnderPressure();

        /// <inheritdoc />
        public void UpdateDeliveryProgress(StreamSequenceToken? earliestSubscriptionToken, DateTime utcNow)
        {
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
