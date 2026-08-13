using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Streaming.EventHubs;

namespace Orleans.Streams
{
    /// <summary>
    /// Persists stream queue checkpoints using Azure Table Storage.
    /// </summary>
    public partial class AzureTableStreamQueueCheckpointer : IStreamQueueCheckpointer<string>
    {
        private readonly IStreamCheckpointStore _store;
        private readonly Func<CancellationToken, Task> _initialize;
        private readonly StreamQueueCheckpointer _inner;

        internal IStreamCheckpointStore Store => _store;

        private AzureTableStreamQueueCheckpointer(
            AzureTableStreamCheckpointerOptions options,
            string streamProviderName,
            string partition,
            string serviceId,
            ILoggerFactory loggerFactory,
            IComparer<string>? defaultComparer = null,
            string? partitionKeyPrefix = null)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentException.ThrowIfNullOrWhiteSpace(streamProviderName);
            ArgumentException.ThrowIfNullOrWhiteSpace(partition);
            ArgumentNullException.ThrowIfNull(loggerFactory);
            if (options.PersistInterval <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    options.PersistInterval,
                    $"{nameof(AzureTableStreamCheckpointerOptions.PersistInterval)} must be greater than zero.");
            }

            var dataManager = new AzureTableDataManager<StreamQueueCheckpointEntity>(
                options,
                loggerFactory.CreateLogger<StreamQueueCheckpointEntity>());
            var store = new AzureTableCheckpointStore(
                dataManager,
                StreamQueueCheckpointEntity.Create(
                    partitionKeyPrefix ?? options.PartitionKeyPrefix,
                    streamProviderName,
                    serviceId,
                    partition));
            _store = store;
            _initialize = store.Initialize;
            _inner = new StreamQueueCheckpointer(
                _store,
                new StreamQueueCheckpointerOptions
                {
                    CheckpointComparer = options.CheckpointComparer ?? defaultComparer,
                    PersistInterval = options.PersistInterval,
                });
            LogCreatingCheckpointer(
                loggerFactory.CreateLogger<AzureTableStreamQueueCheckpointer>(),
                partition,
                streamProviderName,
                serviceId);
        }

        internal AzureTableStreamQueueCheckpointer(
            IStreamCheckpointStore store,
            TimeSpan persistInterval,
            IComparer<string>? checkpointComparer)
        {
            ArgumentNullException.ThrowIfNull(store);
            _store = store;
            _initialize = static _ => Task.CompletedTask;
            _inner = new StreamQueueCheckpointer(
                store,
                new StreamQueueCheckpointerOptions
                {
                    CheckpointComparer = checkpointComparer,
                    PersistInterval = persistInterval,
                });
        }

        /// <inheritdoc />
        public bool CheckpointExists => _inner.CheckpointExists;

        /// <summary>
        /// Creates and initializes an Azure Table stream queue checkpointer.
        /// </summary>
        public static Task<IStreamQueueCheckpointer<string>> Create(
            AzureTableStreamCheckpointerOptions options,
            string streamProviderName,
            string partition,
            string serviceId,
            ILoggerFactory loggerFactory)
        {
            return Create(
                options,
                streamProviderName,
                partition,
                serviceId,
                loggerFactory,
                defaultComparer: null,
                cancellationToken: CancellationToken.None);
        }

        /// <summary>
        /// Creates and initializes an Azure Table stream queue checkpointer.
        /// </summary>
        public static Task<IStreamQueueCheckpointer<string>> Create(
            AzureTableStreamCheckpointerOptions options,
            string streamProviderName,
            string partition,
            string serviceId,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken)
        {
            return Create(
                options,
                streamProviderName,
                partition,
                serviceId,
                loggerFactory,
                defaultComparer: null,
                cancellationToken: cancellationToken);
        }

        internal static async Task<IStreamQueueCheckpointer<string>> Create(
            AzureTableStreamCheckpointerOptions options,
            string streamProviderName,
            string partition,
            string serviceId,
            ILoggerFactory loggerFactory,
            IComparer<string>? defaultComparer,
            string? partitionKeyPrefix = null,
            CancellationToken cancellationToken = default)
        {
            var checkpointer = new AzureTableStreamQueueCheckpointer(
                options,
                streamProviderName,
                partition,
                serviceId,
                loggerFactory,
                defaultComparer,
                partitionKeyPrefix);
            await checkpointer._initialize(cancellationToken);
            return checkpointer;
        }

        /// <inheritdoc />
        public Task<string> Load() => Load(CancellationToken.None);

        /// <inheritdoc />
        public Task<string> Load(CancellationToken cancellationToken) => _inner.Load(cancellationToken);

        /// <inheritdoc />
        public void Update(string offset, DateTime utcNow)
            => Update(offset, utcNow, CancellationToken.None);

        /// <inheritdoc />
        public void Update(string offset, DateTime utcNow, CancellationToken cancellationToken)
            => _inner.Update(offset, utcNow, cancellationToken);

        /// <inheritdoc />
        public Task FlushAsync(CancellationToken cancellationToken)
            => _inner.FlushAsync(cancellationToken);

        private sealed class AzureTableCheckpointStore(
            AzureTableDataManager<StreamQueueCheckpointEntity> dataManager,
            StreamQueueCheckpointEntity entity) : IStreamCheckpointStore
        {
            public StreamQueueCheckpointEntity Entity { get; private set; } = entity;

            public Task Initialize(CancellationToken cancellationToken)
                => dataManager.InitTableAsync(cancellationToken);

            public async ValueTask<StreamCheckpointStoreState> Load(CancellationToken cancellationToken)
            {
                var result = await dataManager.ReadSingleTableEntryAsync(
                    Entity.PartitionKey,
                    Entity.RowKey,
                    cancellationToken);
                if (result.Entity is null)
                {
                    return new(string.Empty, string.Empty);
                }

                Entity = result.Entity;
                return new(Entity.Offset, result.ETag ?? Entity.ETag.ToString());
            }

            public async ValueTask<StreamCheckpointStoreState> Update(
                string checkpoint,
                string expectedVersion,
                CancellationToken cancellationToken)
            {
                var updatedEntity = new StreamQueueCheckpointEntity
                {
                    PartitionKey = Entity.PartitionKey,
                    RowKey = Entity.RowKey,
                    Offset = checkpoint,
                };

                string version;
                if (string.IsNullOrEmpty(expectedVersion))
                {
                    var result = await dataManager.InsertTableEntryAsync(updatedEntity, cancellationToken);
                    if (!result.isSuccess)
                    {
                        return await Load(cancellationToken);
                    }

                    version = result.eTag!;
                }
                else
                {
                    var result = await dataManager.TryUpdateTableEntryAsync(
                        updatedEntity,
                        expectedVersion,
                        cancellationToken);
                    if (!result.isSuccess)
                    {
                        return await Load(cancellationToken);
                    }

                    version = result.eTag!;
                }

                updatedEntity.ETag = new ETag(version);
                Entity = updatedEntity;
                return new(checkpoint, version);
            }
        }

        [LoggerMessage(
            Level = LogLevel.Information,
            Message = "Creating Azure Table checkpointer for partition {Partition} of stream provider {StreamProviderName} with service ID {ServiceId}.")]
        private static partial void LogCreatingCheckpointer(
            ILogger logger,
            string partition,
            string streamProviderName,
            string serviceId);
    }
}
