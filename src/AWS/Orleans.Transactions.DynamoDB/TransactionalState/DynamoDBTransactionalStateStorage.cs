using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Storage;
using Orleans.Transactions.Abstractions;
#if CLUSTERING_DYNAMODB
using Orleans.Clustering.DynamoDB;
#elif PERSISTENCE_DYNAMODB
using Orleans.Persistence.DynamoDB;
#elif REMINDERS_DYNAMODB
using Orleans.Reminders.DynamoDB;
#elif AWSUTILS_TESTS
using Orleans.AWSUtils.Tests;
#elif TRANSACTIONS_DYNAMODB
using Orleans.Transactions.DynamoDB;
#else
#endif
namespace Orleans.Transactions.DynamoDB.TransactionalState;

/// <summary>
/// Provides DynamoDB-backed transactional state storage.
/// </summary>
/// <typeparam name="TState">The state type.</typeparam>
public partial class DynamoDBTransactionalStateStorage<TState> : ITransactionalStateStorage<TState> where TState : class, new()
{
    private const int MaxSnapshotLoadAttempts = 5;
    private const int MaxDynamoDBItemSize = 400 * 1024;
    private const int MaxDynamoDBTransactionSize = 4 * 1024 * 1024;
    private const int TransactionSizeSafetyMargin = 64 * 1024;

    private readonly DynamoDBStorage storage;
    private readonly string tableName;
    private readonly string partitionKey;
    private readonly IGrainStorageSerializer serializer;
    private readonly ILogger<DynamoDBTransactionalStateStorage<TState>> logger;

    // Caches loaded data for this storage instance
    private KeyEntity key = null!;
    private List<KeyValuePair<long, StateEntity>> states = null!;
    private bool requiresReload;

    /// <summary>
    /// Initializes a new instance of the <see cref="DynamoDBTransactionalStateStorage{TState}"/> class.
    /// </summary>
    /// <param name="storage">The DynamoDB storage client.</param>
    /// <param name="options">The provider options.</param>
    /// <param name="partitionKey">The storage partition key.</param>
    /// <param name="logger">The logger.</param>
    public DynamoDBTransactionalStateStorage(DynamoDBStorage storage, DynamoDBTransactionalStorageOptions options, string partitionKey, ILogger<DynamoDBTransactionalStateStorage<TState>> logger)
    {
        this.storage = storage;
        this.tableName = options.TableName;
        this.partitionKey = partitionKey;
        this.serializer = options.GrainStorageSerializer;
        this.logger = logger;
    }

    /// <inheritdoc />
    public async Task<TransactionalStorageLoadResponse<TState>> Load()
    {
        try
        {
            (key, states) = await LoadSnapshotAsync().ConfigureAwait(false);

            if (string.IsNullOrEmpty(key.ETag.ToString()))
            {
                LogDebugLoadedV0Fresh(this.partitionKey);
                this.requiresReload = false;

                // first time load
                return new TransactionalStorageLoadResponse<TState>();
            }

            TState committedState;
            if (this.key.CommittedSequenceId == 0)
            {
                committedState = new TState();
            }
            else
            {
                if (!this.FindState(this.key.CommittedSequenceId, out var pos))
                {
                    var error = $"Storage state corrupted: no record for committed state v{this.key.CommittedSequenceId}";
                    LogCriticalPartitionError(this.partitionKey, error);
                    throw new InvalidOperationException(error);
                }

                committedState = this.ConvertFromStorageFormat<TState>(states[pos].Value);
            }

            var PrepareRecordsToRecover = new List<PendingTransactionState<TState>>();
            for (int i = 0; i < states.Count; i++)
            {
                var kvp = states[i];

                // pending states for already committed transactions can be ignored
                if (kvp.Key <= key.CommittedSequenceId)
                    continue;

                // upon recovery, local non-committed transactions are considered aborted
                if (kvp.Value.TransactionManager is null or { Length: 0 })
                    break;

                ParticipantId tm = this.ConvertFromStorageFormat<ParticipantId>(kvp.Value.TransactionManager);

                PrepareRecordsToRecover.Add(new PendingTransactionState<TState>()
                {
                    SequenceId = kvp.Key,
                    State = this.ConvertFromStorageFormat<TState>(kvp.Value),
                    TimeStamp = kvp.Value.TransactionTimestamp,
                    TransactionId = kvp.Value.TransactionId,
                    TransactionManager = tm
                });
            }

            // no longer needed, ok to GC now
            for (int i = 0; i < states.Count; i++)
            {
                var entity = states[i].Value;
                entity.State = []; // clear the state to free memory
            }

            LogDebugLoadedPartitionKeyRows(this.partitionKey, this.key.CommittedSequenceId, new(states));

            var metadata = this.key.Metadata is { Length: > 0 }
                ? this.ConvertFromStorageFormat<TransactionalStateMetaData>(this.key.Metadata)
                : new TransactionalStateMetaData();
            this.requiresReload = false;
            return new TransactionalStorageLoadResponse<TState>(this.key.ETag.ToString(), committedState, this.key.CommittedSequenceId, metadata, PrepareRecordsToRecover);
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "Error loading transactional state for partition key {PartitionKey}", this.partitionKey);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<string> Store(string? expectedETag, TransactionalStateMetaData metadata, List<PendingTransactionState<TState>>? statesToPrepare, long? commitUpTo, long? abortAfter)
    {
        if (this.requiresReload)
        {
            throw new InvalidOperationException("Load must be called after a failed Store before this storage instance can be reused.");
        }

        var batchOperation = new BatchOperation(this.storage, this.tableName, this.key, this.logger);
        var keyWasNew = !this.key.ETag.HasValue;

        try
        {
            var keyETag = key.ETag?.ToString();
            if ((!string.IsNullOrWhiteSpace(keyETag) || !string.IsNullOrWhiteSpace(expectedETag)) &&
                keyETag != expectedETag)
            {
                throw new ArgumentException("Etag does not match", nameof(expectedETag));
            }

            var serializedMetadata = this.ConvertToStorageFormat(metadata);
            var timestamp = DateTimeOffset.UtcNow;
            var committedSequenceId = commitUpTo.HasValue && commitUpTo.Value > key.CommittedSequenceId
                ? commitUpTo.Value
                : key.CommittedSequenceId;
            this.ValidateKeyItemSize(serializedMetadata, timestamp, committedSequenceId);

            // Store mutates the cached entities while constructing the transaction. If any operation
            // fails, Load must restore the cache before this instance can be used again.
            this.requiresReload = true;

            // assemble all storage operations into a single batch
            // these operations must commit in sequence, but not necessarily atomically
            // so we can split this up if needed

            // first, clean up aborted records
            if (abortAfter.HasValue && states.Count != 0)
            {
                while (states.Count > 0 && states[states.Count - 1].Key > abortAfter)
                {
                    var entity = states[states.Count - 1].Value;
                    var delete = new Amazon.DynamoDBv2.Model.Delete
                    {
                        TableName = this.tableName,
                        Key = this.MakeKeyAttributes(entity.PartitionKey, entity.RowKey),
                        ConditionExpression = $"{DynamoDBTransactionalStateConstants.ETAG_PROPERTY_NAME} = {DynamoDBTransactionalStateConstants.CURRENT_ETAG_ALIAS}",
                        ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                        {
                            [DynamoDBTransactionalStateConstants.CURRENT_ETAG_ALIAS] = new AttributeValue { N = entity.ETag.ToString() }
                        }
                    };

                    await batchOperation.Add(
                        new TransactWriteItem { Delete = delete },
                        entity.PartitionKey,
                        entity.RowKey,
                        entity.StorageSize).ConfigureAwait(false);

                    states.RemoveAt(states.Count - 1);
                    LogTraceDeleteTransaction(entity.PartitionKey, entity.RowKey, entity.TransactionId);
                }
            }

            // second, persist non-obsolete prepare records
            var obsoleteBefore = commitUpTo ?? key.CommittedSequenceId;
            if (statesToPrepare != null)
                foreach (var s in statesToPrepare)
                {
                    if (s.SequenceId >= obsoleteBefore)
                    {
                        if (FindState(s.SequenceId, out var pos))
                        {
                            // overwrite with new pending state
                            StateEntity existing = states[pos].Value;
                            var currentETag = existing.ETag.ToString();
                            existing.TransactionId = s.TransactionId;
                            existing.TransactionTimestamp = s.TimeStamp;
                            existing.TransactionManager = this.ConvertToStorageFormat(s.TransactionManager);
                            existing.SetState(s.State, this.serializer);
                            existing.ETag = existing.ETag + 1;

                            await batchOperation.Add(new TransactWriteItem
                            {
                                Put = new Put
                                {
                                    TableName = this.tableName,
                                    Item = existing.ToStorageFormat(),
                                    ConditionExpression =
                                        $"{DynamoDBTransactionalStateConstants.ETAG_PROPERTY_NAME} = {DynamoDBTransactionalStateConstants.CURRENT_ETAG_ALIAS}",
                                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                                    {
                                        [DynamoDBTransactionalStateConstants.CURRENT_ETAG_ALIAS] = new AttributeValue { N = currentETag }
                                    }
                                }
                            }, existing.PartitionKey, existing.RowKey).ConfigureAwait(false);

                            LogTraceUpdateTransaction(partitionKey, existing.RowKey, existing.TransactionId);
                        }
                        else
                        {
                            var entity = StateEntity.Create(this.serializer, this.partitionKey, s);
                            await batchOperation.Add(new TransactWriteItem
                            {
                                Put = new Put
                                {
                                    TableName = this.tableName,
                                    Item = entity.ToStorageFormat(),
                                    ConditionExpression =
                                        $"attribute_not_exists({DynamoDBTransactionalStateConstants.PARTITION_KEY_PROPERTY_NAME}) AND attribute_not_exists({DynamoDBTransactionalStateConstants.ROW_KEY_PROPERTY_NAME})"
                                }
                            }, entity.PartitionKey, entity.RowKey).ConfigureAwait(false);

                            states.Insert(pos, new KeyValuePair<long, StateEntity>(s.SequenceId, entity));
                            LogTraceInsertTransaction(partitionKey, entity.RowKey, entity.TransactionId);
                        }
                    }
                }

            // third, persist metadata and commit position
            key.Metadata = serializedMetadata;
            key.Timestamp = timestamp;
            key.CommittedSequenceId = committedSequenceId;

            batchOperation.MarkKeyChanged();
            if (keyWasNew)
            {
                LogTraceInsertWithCount(partitionKey, KeyEntity.RK, this.key.CommittedSequenceId,
                    metadata.CommitRecords.Count);
            }
            else
            {
                LogTraceUpdateWithCount(partitionKey, KeyEntity.RK, this.key.CommittedSequenceId,
                    metadata.CommitRecords.Count);
            }

            // fourth, remove obsolete records
            if (states.Count > 0 && states[0].Key < obsoleteBefore)
            {
                FindState(obsoleteBefore, out var pos);
                for (int i = 0; i < pos; i++)
                {
                    var stateToDelete = states[i];
                    var delRequest = new Delete
                    {
                        TableName = this.tableName,
                        Key = this.MakeKeyAttributes(stateToDelete.Value.PartitionKey, stateToDelete.Value.RowKey),
                        ConditionExpression = $"ETag = {DynamoDBTransactionalStateConstants.CURRENT_ETAG_ALIAS}",
                        ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                        {
                            [DynamoDBTransactionalStateConstants.CURRENT_ETAG_ALIAS] = new AttributeValue { N = stateToDelete.Value.ETag.ToString() }
                        }
                    };
                    await batchOperation.Add(
                        new TransactWriteItem { Delete = delRequest },
                        stateToDelete.Value.PartitionKey,
                        stateToDelete.Value.RowKey,
                        stateToDelete.Value.StorageSize).ConfigureAwait(false);

                    LogTraceDeleteTransaction(this.partitionKey, states[i].Value.RowKey, states[i].Value.TransactionId);
                }

                states.RemoveRange(0, pos);
            }

            await batchOperation.Flush().ConfigureAwait(false);

            this.requiresReload = false;
            LogDebugStoredETag(this.partitionKey, this.key.CommittedSequenceId, this.key.ETag);
            return key.ETag!.Value.ToString();
        }
        catch (InconsistentStateException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogErrorTransactionalStateStoreFailed(logger, ex);
            throw;
        }
    }

    private Dictionary<string, AttributeValue> MakeKeyAttributes(string partition, string rowKey)
    {
        return new Dictionary<string, AttributeValue>
        {
            [DynamoDBTransactionalStateConstants.PARTITION_KEY_PROPERTY_NAME] = new AttributeValue { S = partition },
            [DynamoDBTransactionalStateConstants.ROW_KEY_PROPERTY_NAME] = new AttributeValue { S = rowKey }
        };
    }

    /// <summary>
    /// Loads the KeyEntity from DynamoDB.
    /// </summary>
    private async Task<KeyEntity> LoadKeyEntityAsync()
    {
        var keyAttributes = new Dictionary<string, AttributeValue>
        {
            [DynamoDBTransactionalStateConstants.PARTITION_KEY_PROPERTY_NAME] = new AttributeValue { S = partitionKey },
            [DynamoDBTransactionalStateConstants.ROW_KEY_PROPERTY_NAME] = new AttributeValue { S = KeyEntity.RK },
        };

        var keyEntity = await storage.ReadSingleEntryAsync(
            tableName,
            keyAttributes,
            (item) => new KeyEntity(item)).ConfigureAwait(false);
        return keyEntity ?? new KeyEntity(this.partitionKey);
    }

    private async Task<(KeyEntity Key, List<KeyValuePair<long, StateEntity>> States)> LoadSnapshotAsync()
    {
        KeyEntity keyBefore = null!;
        KeyEntity keyAfter = null!;
        for (var attempt = 0; attempt < MaxSnapshotLoadAttempts; attempt++)
        {
            keyBefore = await LoadKeyEntityAsync().ConfigureAwait(false);
            var stateEntities = await LoadStateEntitiesAsync().ConfigureAwait(false);
            keyAfter = await LoadKeyEntityAsync().ConfigureAwait(false);
            if (keyBefore.ETag == keyAfter.ETag)
            {
                return (keyAfter, stateEntities);
            }
        }

        throw new InconsistentStateException(
            "Could not load a consistent DynamoDB transactional state snapshot.",
            storedEtag: keyBefore.ETag?.ToString() ?? "null",
            currentEtag: keyAfter.ETag?.ToString() ?? "null");
    }

    /// <summary>
    /// Loads all unpublished StateEntity records from DynamoDB.
    /// </summary>
    private async Task<List<KeyValuePair<long, StateEntity>>> LoadStateEntitiesAsync()
    {
        var keyConditionExpression =
            $"{DynamoDBTransactionalStateConstants.PARTITION_KEY_PROPERTY_NAME} = :partitionKey and {DynamoDBTransactionalStateConstants.ROW_KEY_PROPERTY_NAME} between :minRowKeyPrefix and :maxRowKeyPrefix";
        var keys = new Dictionary<string, AttributeValue>
        {
            { ":partitionKey", new AttributeValue { S = this.partitionKey } },
            { ":minRowKeyPrefix", new AttributeValue { S = StateEntity.ROW_KEY_MIN } },
            { ":maxRowKeyPrefix", new AttributeValue { S = StateEntity.ROW_KEY_MAX } },
        };

        var records = new List<StateEntity>();
        try
        {
            records = await this.storage.QueryAllAsync(
                this.tableName,
                keys,
                keyConditionExpression,
                (fields) => new StateEntity(fields)).ConfigureAwait(false);

            return records.Select(record => new KeyValuePair<long, StateEntity>(record.SequenceId, record)).ToList();
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "Read transactional states failed.");
            throw;
        }
    }

    private T ConvertFromStorageFormat<T>(StateEntity entity)
    {
        T dataValue = default!;
        try
        {
            if (entity.State is { Length: > 0 })
                dataValue = this.serializer.Deserialize<T>(entity.State)!;
        }
        catch (Exception exc)
        {
            var message = $"Unable to convert from storage format GrainStateEntity.Data={entity.State}";
            if (dataValue is not null)
            {
                message += $"Data Value={dataValue} Type={dataValue.GetType()}";
            }

            LogError(logger, message);
            throw new AggregateException(message, exc);
        }

        return dataValue;
    }

    private T ConvertFromStorageFormat<T>(byte[] value)
    {
        T dataValue = default!;

        try
        {
            if (value is { Length: > 0 })
                dataValue = this.serializer.Deserialize<T>(value)!;
        }
        catch (Exception exc)
        {
            var message = $"Unable to convert from storage format, Data={value}, DataLen={value?.Length ?? -1}, StateType={typeof(T)}";
            LogError(logger, message);
            throw new AggregateException(message, exc);
        }

        return dataValue;
    }

    private byte[] ConvertToStorageFormat<T>(T value) => this.serializer.Serialize(value).ToArray();

    private void ValidateKeyItemSize(byte[] metadata, DateTimeOffset timestamp, long committedSequenceId)
    {
        var candidate = new KeyEntity(this.partitionKey)
        {
            CommittedSequenceId = committedSequenceId,
            Metadata = metadata,
            Timestamp = timestamp,
            ETag = long.MaxValue
        };

        ValidateItemSize(candidate.ToStorageFormat(), nameof(metadata));
    }

    private static int ValidateItemSize(Dictionary<string, AttributeValue> item, string parameterName)
    {
        var itemSize = StateEntity.GetItemSize(item);
        if (itemSize > MaxDynamoDBItemSize)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                itemSize,
                $"DynamoDB items cannot exceed {MaxDynamoDBItemSize} bytes.");
        }

        return itemSize;
    }

    private bool FindState(long sequenceId, out int pos)
    {
        pos = 0;
        while (pos < states.Count)
        {
            switch (states[pos].Key.CompareTo(sequenceId))
            {
                case 0:
                    return true;
                case -1:
                    pos++;
                    continue;
                case 1:
                    return false;
            }
        }
        return false;
    }

    private sealed class BatchOperation(
        DynamoDBStorage storage,
        string tableName,
        KeyEntity key,
        ILogger logger)
    {
        private const int MaxDataOperations = 99;
        private const int MaxDataSize =
            MaxDynamoDBTransactionSize - MaxDynamoDBItemSize - TransactionSizeSafetyMargin;

        private readonly List<(TransactWriteItem Item, string PartitionKey, string RowKey)> operations = [];
        private int operationsSize;
        private bool keyChanged;

        public async ValueTask Add(
            TransactWriteItem operation,
            string partitionKey,
            string rowKey,
            int? affectedItemSize = null)
        {
            var operationSize = GetOperationSize(operation, affectedItemSize);
            if (this.operations.Count > 0 && this.operationsSize + operationSize > MaxDataSize)
            {
                await FlushCore().ConfigureAwait(false);
            }

            this.operations.Add((operation, partitionKey, rowKey));
            this.operationsSize += operationSize;
            if (this.operations.Count == MaxDataOperations)
            {
                await FlushCore().ConfigureAwait(false);
            }
        }

        public void MarkKeyChanged() => this.keyChanged = true;

        public Task Flush() => this.operations.Count > 0 || this.keyChanged ? FlushCore() : Task.CompletedTask;

        private async Task FlushCore()
        {
            var currentETag = key.ETag;
            var nextETag = checked(currentETag.GetValueOrDefault(-1) + 1);
            key.ETag = nextETag;
            var keyItem = key.ToStorageFormat();
            key.ETag = currentETag;
            var keyItemSize = ValidateItemSize(keyItem, "metadata");
            if (this.operationsSize + keyItemSize > MaxDynamoDBTransactionSize)
            {
                throw new InvalidOperationException("DynamoDB transaction size calculation exceeded the service limit.");
            }

            var keyPut = new Put
            {
                TableName = tableName,
                Item = keyItem
            };

            if (currentETag.HasValue)
            {
                keyPut.ConditionExpression =
                    $"{DynamoDBTransactionalStateConstants.ETAG_PROPERTY_NAME} = {DynamoDBTransactionalStateConstants.CURRENT_ETAG_ALIAS}";
                keyPut.ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [DynamoDBTransactionalStateConstants.CURRENT_ETAG_ALIAS] = new AttributeValue { N = currentETag.Value.ToString() }
                };
            }
            else
            {
                keyPut.ConditionExpression =
                    $"attribute_not_exists({DynamoDBTransactionalStateConstants.PARTITION_KEY_PROPERTY_NAME}) AND attribute_not_exists({DynamoDBTransactionalStateConstants.ROW_KEY_PROPERTY_NAME})";
            }

            var transaction = this.operations.Select(operation => operation.Item).ToList();
            transaction.Add(new TransactWriteItem { Put = keyPut });

            try
            {
                await storage.WriteTxAsync(transaction).ConfigureAwait(false);
                key.ETag = nextETag;
                this.keyChanged = false;

                if (logger.IsEnabled(LogLevel.Trace))
                {
                    for (var i = 0; i < this.operations.Count; i++)
                    {
                        var operation = this.operations[i];
                        LogTraceBatchOpOk(logger, operation.PartitionKey, operation.RowKey, i);
                    }

                    LogTraceBatchOpOk(logger, key.PartitionKey, key.RowKey, this.operations.Count);
                }

                this.operations.Clear();
                this.operationsSize = 0;
            }
            catch (TransactionCanceledException exception) when (IsStorageConflict(exception))
            {
                LogFailures();
                var conflictMessage = GetConflictMessage(exception, keyPut);
                LogWarningTransactionalStateConflict(logger, conflictMessage);
                throw new InconsistentStateException(
                    conflictMessage,
                    storedEtag: "Unknown",
                    currentEtag: currentETag?.ToString() ?? "null",
                    exception);
            }
            catch
            {
                LogFailures();
                throw;
            }
        }

        private void LogFailures()
        {
            if (!logger.IsEnabled(LogLevel.Trace))
            {
                return;
            }

            for (var i = 0; i < this.operations.Count; i++)
            {
                var operation = this.operations[i];
                LogTraceBatchOpFailed(logger, operation.PartitionKey, operation.RowKey, i);
            }

            LogTraceBatchOpFailed(logger, key.PartitionKey, key.RowKey, this.operations.Count);
        }

        private string GetConflictMessage(TransactionCanceledException exception, Put keyPut)
        {
            var operations = new string[this.operations.Count + 1];

            for (var index = 0; index < this.operations.Count; index++)
            {
                operations[index] = FormatOperationDiagnostic(
                    index,
                    this.operations[index].Item,
                    this.operations[index].RowKey,
                    "Data",
                    exception);
            }

            operations[^1] = FormatOperationDiagnostic(
                this.operations.Count,
                new TransactWriteItem { Put = keyPut },
                key.RowKey,
                "KeySynchronizer",
                exception);
            return $"DynamoDB transactional state storage conflict. PartitionKey={key.PartitionKey}. {string.Join(' ', operations)}";
        }

        private static string FormatOperationDiagnostic(
            int index,
            TransactWriteItem operation,
            string rowKey,
            string role,
            TransactionCanceledException exception)
        {
            var (operationType, conditionExpression, expressionAttributeValues) = operation.Put is { } put
                ? ("Put", put.ConditionExpression, put.ExpressionAttributeValues)
                : ("Delete", operation.Delete?.ConditionExpression, operation.Delete?.ExpressionAttributeValues);
            var reason = exception.CancellationReasons is { Count: > 0 } reasons && index < reasons.Count
                ? reasons[index]
                : null;
            var condition = string.IsNullOrEmpty(conditionExpression)
                ? string.Empty
                : $" Condition={conditionExpression}";
            var expectedETag = expressionAttributeValues?.TryGetValue(
                DynamoDBTransactionalStateConstants.CURRENT_ETAG_ALIAS,
                out var value) is true
                    ? $" ExpectedETag={value.N}"
                    : string.Empty;
            return $"TransactWriteItems[{index}] Operation={operationType} Role={role} RowKey={rowKey}"
                + $"{condition}{expectedETag} CancellationReasonCode={reason?.Code ?? "Unavailable"}"
                + $" CancellationReasonMessage={reason?.Message ?? "Unavailable"}.";
        }

        private static bool IsStorageConflict(TransactionCanceledException exception)
        {
            return exception.CancellationReasons?.Any(
                reason => string.Equals(reason.Code, "ConditionalCheckFailed", StringComparison.Ordinal)) is true
                || exception.Message.Contains("ConditionalCheckFailed", StringComparison.Ordinal);
        }

        private static int GetOperationSize(TransactWriteItem operation, int? affectedItemSize)
        {
            if (operation.Put?.Item is { } item)
            {
                return ValidateItemSize(item, "state");
            }
            if (operation.Delete is not null)
            {
                return affectedItemSize ?? MaxDynamoDBItemSize;
            }

            throw new InvalidOperationException("Only DynamoDB put and delete operations are supported.");
        }
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "{Partition} Loaded v0, fresh"
    )]
    private partial void LogDebugLoadedV0Fresh(string partition);

    [LoggerMessage(
        Level = LogLevel.Critical,
        Message = "{Partition} {Error}"
    )]
    private partial void LogCriticalPartitionError(string partition, string error);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "{Message}"
    )]
    private static partial void LogError(ILogger logger, string message);

    private readonly struct StatesLogRecord(List<KeyValuePair<long, StateEntity>> states)
    {
        public override string ToString() => string.Join(",", states.Select(s => s.Key.ToString("x16")));
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "{PartitionKey} Loaded v{CommittedSequenceId} rows={Data}"
    )]
    private partial void LogDebugLoadedPartitionKeyRows(string partitionKey, long committedSequenceId, StatesLogRecord data);

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "{PartitionKey}.{RowKey} Delete {TransactionId}"
    )]
    private partial void LogTraceDeleteTransaction(string partitionKey, string rowKey, string transactionId);

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "{PartitionKey}.{RowKey} Update {TransactionId}"
    )]
    private partial void LogTraceUpdateTransaction(string partitionKey, string rowKey, string transactionId);

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "{PartitionKey}.{RowKey} Insert {TransactionId}"
    )]
    private partial void LogTraceInsertTransaction(string partitionKey, string rowKey, string transactionId);

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "{PartitionKey}.{RowKey} Insert. v{CommittedSequenceId}, {CommitRecordsCount}c"
    )]
    private partial void LogTraceInsertWithCount(string partitionKey, string rowKey, long committedSequenceId, int commitRecordsCount);

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "{PartitionKey}.{RowKey} Update. v{CommittedSequenceId}, {CommitRecordsCount}c"
    )]
    private partial void LogTraceUpdateWithCount(string partitionKey, string rowKey, long committedSequenceId, int commitRecordsCount);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "{PartitionKey} Stored v{CommittedSequenceId} eTag={ETag}"
    )]
    private partial void LogDebugStoredETag(string partitionKey, long committedSequenceId, long? eTag);

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "{PartitionKey}.{RowKey} batch-op ok {BatchCount}"
    )]
    private static partial void LogTraceBatchOpOk(ILogger logger, string partitionKey, string rowKey, int batchCount);

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "{PartitionKey}.{RowKey} batch-op failed {BatchCount}"
    )]
    private static partial void LogTraceBatchOpFailed(ILogger logger, string partitionKey, string rowKey, int batchCount);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "{ConflictMessage}"
    )]
    private static partial void LogWarningTransactionalStateConflict(ILogger logger, string conflictMessage);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Transactional state store failed."
    )]
    private static partial void LogErrorTransactionalStateStoreFailed(ILogger logger, Exception ex);
}
