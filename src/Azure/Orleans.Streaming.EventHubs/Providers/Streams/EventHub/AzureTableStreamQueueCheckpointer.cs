using System;
using System.Collections.Generic;
using System.Net;
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
        private readonly AzureTableDataManager<StreamQueueCheckpointEntity> _dataManager;
        private readonly ILogger<AzureTableStreamQueueCheckpointer> _logger;
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

            _persistInterval = options.PersistInterval;
            _checkpointComparer = options.CheckpointComparer ?? defaultComparer;
            _logger = loggerFactory.CreateLogger<AzureTableStreamQueueCheckpointer>();
            _dataManager = new AzureTableDataManager<StreamQueueCheckpointEntity>(
                options,
                loggerFactory.CreateLogger<StreamQueueCheckpointEntity>());
            _entity = StreamQueueCheckpointEntity.Create(
                partitionKeyPrefix ?? options.PartitionKeyPrefix,
                streamProviderName,
                serviceId,
                partition);
            LogCreatingCheckpointer(
                _logger,
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
            IComparer<string>? defaultComparer,
            string? partitionKeyPrefix = null)
        {
            var checkpointer = new AzureTableStreamQueueCheckpointer(
                options,
                streamProviderName,
                partition,
                serviceId,
                loggerFactory,
                defaultComparer,
                partitionKeyPrefix);
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
        [Obsolete("Use the overload which accepts a CancellationToken.")]
        public Task Reset() => Reset(CancellationToken.None);

        /// <inheritdoc />
        public async Task Reset(CancellationToken cancellationToken)
        {
            Task inProgressSave;
            lock (_lock)
            {
                inProgressSave = _inProgressSave;
            }

            try
            {
                await inProgressSave.WaitAsync(cancellationToken);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                LogWarningCheckpointSaveFailedBeforeReset(_logger, exception);
            }

            try
            {
                await _dataManager.DeleteTableEntryAsync(_entity, ETag.All);
            }
            catch (RequestFailedException exception) when (
                exception.Status is (int)HttpStatusCode.NotFound or (int)HttpStatusCode.PreconditionFailed)
            {
                LogDebugCheckpointResetAlreadySatisfied(_logger, exception);
            }
            finally
            {
                lock (_lock)
                {
                    _entity.Offset = string.Empty;
                    _entity.ETag = default;
                    _latestCheckpoint = string.Empty;
                    _persistedCheckpoint = string.Empty;
                    _throttleSavesUntilUtc = null;
                    _inProgressSave = Task.CompletedTask;
                }
            }
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

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "The in-progress checkpoint save failed before the checkpoint was reset.")]
        private static partial void LogWarningCheckpointSaveFailedBeforeReset(ILogger logger, Exception exception);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "The checkpoint reset was already satisfied by concurrent storage state.")]
        private static partial void LogDebugCheckpointResetAlreadySatisfied(ILogger logger, Exception exception);
    }
}
