using System;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Configuration;
using Orleans.Streams;

namespace Orleans.Streaming.Kinesis
{
    /// <summary>
    /// Persists stream queue checkpoints in DynamoDB.
    /// </summary>
    internal sealed class DynamoDBStreamQueueCheckpointer : IStreamQueueCheckpointer<string>
    {
        private readonly StreamQueueCheckpointer _inner;

        internal DynamoDBStreamQueueCheckpointer(
            IDynamoDBStreamCheckpointStore store,
            DynamoDBStreamQueueCheckpointerOptions options)
        {
            _inner = new StreamQueueCheckpointer(
                new StreamCheckpointStoreAdapter(store),
                new StreamQueueCheckpointerOptions
                {
                    CheckpointComparer = StreamCheckpointComparers.Numeric,
                    PersistInterval = options.PersistInterval,
                });
        }

        /// <inheritdoc />
        public bool CheckpointExists => _inner.CheckpointExists;

        internal static async Task<IStreamQueueCheckpointer<string>> Create(
            IDynamoDBStreamCheckpointStore store,
            DynamoDBStreamQueueCheckpointerOptions options)
            => await Create(store, options, CancellationToken.None);

        internal static async Task<IStreamQueueCheckpointer<string>> Create(
            IDynamoDBStreamCheckpointStore store,
            DynamoDBStreamQueueCheckpointerOptions options,
            CancellationToken cancellationToken)
        {
            var result = new DynamoDBStreamQueueCheckpointer(store, options);
            _ = await result.Load(cancellationToken);
            return result;
        }

        /// <inheritdoc />
        [Obsolete("Use the overload which accepts a CancellationToken.")]
        public Task<string> Load() => Load(CancellationToken.None);

        /// <inheritdoc />
        public Task<string> Load(CancellationToken cancellationToken) => _inner.Load(cancellationToken);

        /// <inheritdoc />
        [Obsolete("Use the overload which accepts a CancellationToken.")]
        public void Update(string offset, DateTime utcNow)
            => Update(offset, utcNow, CancellationToken.None);

        /// <inheritdoc />
        public void Update(string offset, DateTime utcNow, CancellationToken cancellationToken)
            => _inner.Update(offset, utcNow, cancellationToken);

        /// <inheritdoc />
        public Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

        private sealed class StreamCheckpointStoreAdapter(IDynamoDBStreamCheckpointStore store) : IStreamCheckpointStore
        {
            public async ValueTask<StreamCheckpointStoreState> Load(CancellationToken cancellationToken)
            {
                var checkpoint = await store.Load(cancellationToken).ConfigureAwait(false);
                return new(checkpoint, checkpoint);
            }

            public async ValueTask<StreamCheckpointStoreState> Update(
                string checkpoint,
                string expectedVersion,
                CancellationToken cancellationToken)
            {
                var persistedCheckpoint = await store.Update(checkpoint, expectedVersion, cancellationToken).ConfigureAwait(false);
                return new(persistedCheckpoint, persistedCheckpoint);
            }
        }
    }
}
