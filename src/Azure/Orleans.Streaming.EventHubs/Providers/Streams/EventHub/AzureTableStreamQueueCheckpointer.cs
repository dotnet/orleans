using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Streaming.EventHubs;

namespace Orleans.Streams;

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
    private int _pendingResetCount;
    private long _updateGeneration;

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
        _dataManager = new AzureTableDataManager<StreamQueueCheckpointEntity>(
            options,
            loggerFactory.CreateLogger<StreamQueueCheckpointEntity>());
        _entity = StreamQueueCheckpointEntity.Create(
            partitionKeyPrefix ?? options.PartitionKeyPrefix,
            streamProviderName,
            serviceId,
            partition);
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
        => Create(
            options,
            streamProviderName,
            partition,
            serviceId,
            loggerFactory,
            CancellationToken.None);

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
        await checkpointer._dataManager.InitTableAsync(cancellationToken);
        return checkpointer;
    }

    /// <inheritdoc />
    [Obsolete("Use the overload which accepts a CancellationToken.")]
    public Task<string> Load() => Load(CancellationToken.None);

    /// <inheritdoc />
    public async Task<string> Load(CancellationToken cancellationToken)
    {
        var result = await _dataManager.ReadSingleTableEntryAsync(
            _entity.PartitionKey,
            _entity.RowKey,
            cancellationToken);
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
    public Task Reset(CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            _pendingResetCount++;
            _throttleSavesUntilUtc = DateTime.MaxValue;
            var inProgressSave = _inProgressSave;
            _inProgressSave = completion.Task;
            RunReset(
                completion,
                inProgressSave,
                _inProgressSave,
                _latestCheckpoint,
                _updateGeneration,
                cancellationToken).Ignore();
            completion.Task.Ignore();
        }

        return completion.Task.WaitAsync(cancellationToken);
    }

    private async Task RunReset(
        TaskCompletionSource completion,
        Task inProgressSave,
        Task resetTask,
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

            while (true)
            {
                string checkpoint;
                lock (_lock)
                {
                    if (!ReferenceEquals(resetTask, _inProgressSave)
                        || string.Equals(_persistedCheckpoint, _latestCheckpoint, StringComparison.Ordinal))
                    {
                        CompleteReset();
                        completion.TrySetResult();
                        return;
                    }

                    checkpoint = _latestCheckpoint;
                }

                await Save(checkpoint, cancellationToken);
            }
        }
        catch (OperationCanceledException exception)
        {
            await CompleteFailedReset(
                completion,
                resetTask,
                exception,
                canceled: true);
        }
        catch (Exception exception)
        {
            await CompleteFailedReset(
                completion,
                resetTask,
                exception,
                canceled: false);
        }
    }

    private async Task CompleteFailedReset(
        TaskCompletionSource completion,
        Task resetTask,
        Exception resetFailure,
        bool canceled)
    {
        try
        {
            while (true)
            {
                string checkpoint;
                lock (_lock)
                {
                    if (!ReferenceEquals(resetTask, _inProgressSave)
                        || string.Equals(_persistedCheckpoint, _latestCheckpoint, StringComparison.Ordinal))
                    {
                        CompleteReset();
                        if (canceled)
                        {
                            completion.TrySetCanceled(
                                ((OperationCanceledException)resetFailure).CancellationToken);
                        }
                        else
                        {
                            completion.TrySetException(resetFailure);
                        }

                        return;
                    }

                    checkpoint = _latestCheckpoint;
                }

                await Save(checkpoint, CancellationToken.None);
            }
        }
        catch (Exception persistenceFailure)
        {
            lock (_lock)
            {
                CompleteReset();
                completion.TrySetException(
                    new AggregateException(resetFailure, persistenceFailure));
            }
        }
    }

    private void CompleteReset()
    {
        _pendingResetCount--;
        if (_pendingResetCount == 0)
        {
            _throttleSavesUntilUtc = null;
        }
    }

    /// <inheritdoc />
    public void Update(string offset, DateTime utcNow)
    {
        ArgumentNullException.ThrowIfNull(offset);

        lock (_lock)
        {
            if (string.Equals(_latestCheckpoint, offset, StringComparison.Ordinal))
            {
                if (string.Equals(_persistedCheckpoint, offset, StringComparison.Ordinal)
                    || !_inProgressSave.IsCompleted
                    || (_throttleSavesUntilUtc.HasValue && _throttleSavesUntilUtc.Value > utcNow))
                {
                    return;
                }

                _throttleSavesUntilUtc = utcNow + _persistInterval;
                _inProgressSave = Save(offset, CancellationToken.None);
                _inProgressSave.Ignore();
                return;
            }

            if (_checkpointComparer is { } comparer
                    && !string.IsNullOrEmpty(_latestCheckpoint)
                    && comparer.Compare(offset, _latestCheckpoint) <= 0)
            {
                return;
            }

            _latestCheckpoint = offset;
            _updateGeneration++;
            _entity.Offset = offset;
            if (!_inProgressSave.IsCompleted
                || (_throttleSavesUntilUtc.HasValue && _throttleSavesUntilUtc.Value > utcNow))
            {
                return;
            }

            _throttleSavesUntilUtc = utcNow + _persistInterval;
            _inProgressSave = Save(offset, CancellationToken.None);
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

                _inProgressSave = Save(_latestCheckpoint, cancellationToken);
                _inProgressSave.Ignore();
                retryingSave = true;
            }
        }
    }

    private async Task Save(string checkpoint, CancellationToken cancellationToken)
    {
        var entity = CreateWriteEntity(checkpoint);
        await _dataManager.UpsertTableEntryAsync(entity, cancellationToken);
        lock (_lock)
        {
            _entity.Offset = checkpoint;
            _persistedCheckpoint = checkpoint;
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
        lock (_lock)
        {
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
            var entity = CreateWriteEntity(string.Empty);
            await _dataManager.UpsertTableEntryAsync(entity, cancellationToken);
            lock (_lock)
            {
                _persistedCheckpoint = string.Empty;
                if (string.IsNullOrEmpty(_latestCheckpoint))
                {
                    _entity.Offset = string.Empty;
                }
            }
        }
        catch
        {
            lock (_lock)
            {
                RestoreLatestCheckpoint(rollbackCheckpoint);
            }

            throw;
        }
    }

    private void RestoreLatestCheckpoint(string latestCheckpoint)
    {
        if (_checkpointComparer is { } comparer
            && !string.IsNullOrEmpty(_latestCheckpoint)
            && !string.IsNullOrEmpty(latestCheckpoint))
        {
            if (comparer.Compare(_latestCheckpoint, latestCheckpoint) < 0)
            {
                _latestCheckpoint = latestCheckpoint;
            }

            return;
        }

        if (string.IsNullOrEmpty(_latestCheckpoint))
        {
            _latestCheckpoint = latestCheckpoint;
        }
    }

    private StreamQueueCheckpointEntity CreateWriteEntity(string checkpoint)
    {
        lock (_lock)
        {
            return new StreamQueueCheckpointEntity
            {
                PartitionKey = _entity.PartitionKey,
                RowKey = _entity.RowKey,
                Offset = checkpoint,
            };
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
