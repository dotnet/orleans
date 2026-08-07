using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
        private readonly AzureTableDataManager<StreamQueueCheckpointEntity> _dataManager;
        private readonly TimeSpan _persistInterval;
        private readonly IComparer<string>? _checkpointComparer;
        private readonly object _lock = new();

        private StreamQueueCheckpointEntity _entity;
        private Task _inProgressSave = Task.CompletedTask;
        private DateTime? _throttleSavesUntilUtc;
        private string _latestCheckpoint = string.Empty;
        private string _persistedCheckpoint = string.Empty;

        private AzureTableStreamQueueCheckpointer(
            AzureTableStreamCheckpointerOptions options,
            string streamProviderName,
            string partition,
            string serviceId,
            ILoggerFactory loggerFactory,
            IComparer<string>? defaultComparer = null)
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

            _persistInterval = options.PersistInterval;
            _checkpointComparer = options.CheckpointComparer ?? defaultComparer;
            _dataManager = new AzureTableDataManager<StreamQueueCheckpointEntity>(
                options,
                loggerFactory.CreateLogger<StreamQueueCheckpointEntity>());
            _entity = StreamQueueCheckpointEntity.Create(streamProviderName, serviceId, partition);
            LogCreatingCheckpointer(
                loggerFactory.CreateLogger<AzureTableStreamQueueCheckpointer>(),
                partition,
                streamProviderName,
                serviceId);
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
            return Create(options, streamProviderName, partition, serviceId, loggerFactory, defaultComparer: null);
        }

        internal static async Task<IStreamQueueCheckpointer<string>> Create(
            AzureTableStreamCheckpointerOptions options,
            string streamProviderName,
            string partition,
            string serviceId,
            ILoggerFactory loggerFactory,
            IComparer<string>? defaultComparer)
        {
            var checkpointer = new AzureTableStreamQueueCheckpointer(
                options,
                streamProviderName,
                partition,
                serviceId,
                loggerFactory,
                defaultComparer);
            await checkpointer._dataManager.InitTableAsync();
            return checkpointer;
        }

        /// <inheritdoc />
        public async Task<string> Load()
        {
            var result = await _dataManager.ReadSingleTableEntryAsync(_entity.PartitionKey, _entity.RowKey);
            var checkpoint = result.Entity?.Offset ?? string.Empty;
            lock (_lock)
            {
                if (result.Entity is not null)
                {
                    _entity = result.Entity;
                }

                _latestCheckpoint = checkpoint;
                _persistedCheckpoint = checkpoint;
            }

            return checkpoint;
        }

        /// <inheritdoc />
        public void Update(string offset, DateTime utcNow)
        {
            ArgumentNullException.ThrowIfNull(offset);

            lock (_lock)
            {
                if (string.Equals(_latestCheckpoint, offset, StringComparison.Ordinal)
                    || (_checkpointComparer is { } comparer
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

                _throttleSavesUntilUtc = utcNow + _persistInterval;
                _inProgressSave = Save(offset);
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

                    if (string.Equals(_persistedCheckpoint, _latestCheckpoint, StringComparison.Ordinal))
                    {
                        return;
                    }

                    _inProgressSave = Save(_latestCheckpoint);
                    retryingSave = true;
                }
            }
        }

        private async Task Save(string checkpoint)
        {
            _entity.Offset = checkpoint;
            await _dataManager.UpsertTableEntryAsync(_entity);
            lock (_lock)
            {
                _persistedCheckpoint = checkpoint;
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
