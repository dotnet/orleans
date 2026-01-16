using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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

public partial class DynamoDBTransactionalStateStorage<TState> : ITransactionalStateStorage<TState> where TState : class, new()
{
    private readonly DynamoDBStorage storage;
    private readonly string tableName;
    private readonly string partitionKey;
    private readonly IGrainStorageSerializer serializer;
    private readonly ILogger<DynamoDBTransactionalStateStorage<TState>> logger;

    // Caches loaded data for this storage instance
    private KeyEntity key;
    private List<KeyValuePair<long, StateEntity>> states;

    public DynamoDBTransactionalStateStorage(DynamoDBStorage storage, DynamoDBTransactionalStorageOptions options, string partitionKey, ILogger<DynamoDBTransactionalStateStorage<TState>> logger)
    {
        this.storage = storage;
        this.tableName = options.TableName;
        this.partitionKey = partitionKey;
        this.serializer = options.GrainStorageSerializer;
        this.logger = logger;
    }

    public async Task<TransactionalStorageLoadResponse<TState>> Load()
    {
        try
        {
            // Load key record and all prepared state records
            var keyEntityTask = LoadKeyEntityAsync();
            var stateEntitiesTask = LoadStateEntitiesAsync();
            key = await keyEntityTask.ConfigureAwait(false);
            states = await stateEntitiesTask.ConfigureAwait(false);

            if (string.IsNullOrEmpty(key.ETag.ToString()))
            {
                LogDebugLoadedV0Fresh(this.partitionKey);

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

            TransactionalStateMetaData metadata = this.ConvertFromStorageFormat<TransactionalStateMetaData>(this.key.Metadata);
            return new TransactionalStorageLoadResponse<TState>(this.key.ETag.ToString(), committedState, this.key.CommittedSequenceId, metadata, PrepareRecordsToRecover);
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "Error loading transactional state for partition key {PartitionKey}", this.partitionKey);
            throw;
        }
    }

    public async Task<string> Store(string expectedETag, TransactionalStateMetaData metadata, List<PendingTransactionState<TState>> statesToPrepare, long? commitUpTo, long? abortAfter)
    {
        var transactItems = new List<(TransactWriteItem Item, string PartitionKey, string RowKey)>();

        try
        {
            var keyETag = key.ETag.ToString();
            if ((!string.IsNullOrWhiteSpace(keyETag) || !string.IsNullOrWhiteSpace(expectedETag)) &&
                keyETag != expectedETag)
            {
                throw new ArgumentException("Etag does not match", nameof(expectedETag));
            }

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

                    transactItems.Add((new TransactWriteItem { Delete = delete }, entity.PartitionKey, entity.RowKey));

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

                            transactItems.Add((new TransactWriteItem
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
                            }, existing.PartitionKey, existing.RowKey));

                            LogTraceUpdateTransaction(partitionKey, existing.RowKey, existing.TransactionId);
                        }
                        else
                        {
                            var entity = StateEntity.Create(this.serializer, this.partitionKey, s);
                            transactItems.Add((new TransactWriteItem
                            {
                                Put = new Put { TableName = this.tableName, Item = entity.ToStorageFormat(), }
                            }, entity.PartitionKey, entity.RowKey));

                            states.Insert(pos, new KeyValuePair<long, StateEntity>(s.SequenceId, entity));
                            LogTraceInsertTransaction(partitionKey, entity.RowKey, entity.TransactionId);
                        }
                    }
                }

            // third, persist metadata and commit position
            key.Metadata = this.ConvertToStorageFormat(metadata);
            key.Timestamp = DateTimeOffset.UtcNow;
            if (commitUpTo.HasValue && commitUpTo.Value > key.CommittedSequenceId)
            {
                key.CommittedSequenceId = commitUpTo.Value;
            }

            var existingETag = key.ETag.ToString();
            if (string.IsNullOrWhiteSpace(existingETag))
            {
                this.key.ETag = 0;
                var keyPutRequest = new Put
                {
                    TableName = this.tableName,
                    Item = key.ToStorageFormat(),
                };
                transactItems.Add((new TransactWriteItem { Put = keyPutRequest }, this.partitionKey, KeyEntity.RK));
                LogTraceInsertWithCount(partitionKey, KeyEntity.RK, this.key.CommittedSequenceId,
                    metadata.CommitRecords.Count);
            }
            else
            {
                this.key.ETag = this.key.ETag + 1;
                var keyPutRequest = new Put
                {
                    TableName = this.tableName,
                    Item = key.ToStorageFormat(),
                    ConditionExpression = $"ETag = {DynamoDBTransactionalStateConstants.CURRENT_ETAG_ALIAS}",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [DynamoDBTransactionalStateConstants.CURRENT_ETAG_ALIAS] = new AttributeValue { N = existingETag } }
                };
                transactItems.Add((new TransactWriteItem { Put = keyPutRequest }, this.partitionKey, KeyEntity.RK));
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
                    transactItems.Add((new TransactWriteItem { Delete = delRequest }, stateToDelete.Value.PartitionKey, stateToDelete.Value.RowKey));

                    LogTraceDeleteTransaction(this.partitionKey, states[i].Value.RowKey, states[i].Value.TransactionId);
                }

                states.RemoveRange(0, pos);
            }

            this.logger.LogInformation("Storing {Count} items in DynamoDB for partition {PartitionKey}", transactItems.Count, this.partitionKey);

            const int txChunkSize = 100;
            var txItems = transactItems.Select(item => item.Item).ToList();
            for (int i = 0; i < txItems.Count; i += txChunkSize)
            {
                var batch = txItems.Skip(i).Take(txChunkSize).ToList();
                await this.storage.WriteTxAsync(batch).ConfigureAwait(false);
                LogTraceBatchOpOk(logger, transactItems[i].PartitionKey, transactItems[i].RowKey, batch.Count);
            }

            LogDebugStoredETag(this.partitionKey, this.key.CommittedSequenceId, this.key.ETag);
            return key.ETag.ToString();
        }
        catch (Exception ex)
        {
            if (logger.IsEnabled(LogLevel.Trace))
            {
                for (int i = 0; i < transactItems.Count; i++)
                {
                    LogTraceBatchOpFailed(logger, transactItems[i].PartitionKey, transactItems[i].RowKey, i);
                }
            }

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
        T dataValue = default;
        try
        {
            if (entity.State is { Length: > 0 })
                dataValue = this.serializer.Deserialize<T>(entity.State);
        }
        catch (Exception exc)
        {
            var sb = new StringBuilder();
            sb.AppendFormat("Unable to convert from storage format GrainStateEntity.Data={0}", entity.State);

            if (dataValue != null)
            {
                sb.Append($"Data Value={dataValue} Type={dataValue.GetType()}");
            }

            var message = sb.ToString();
            LogError(logger, message);
            throw new AggregateException(message, exc);
        }

        return dataValue;
    }

    private T ConvertFromStorageFormat<T>(byte[] value)
    {
        T dataValue = default;

        try
        {
            if (value is { Length: > 0 })
                dataValue = this.serializer.Deserialize<T>(value);
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
        Level = LogLevel.Error,
        Message = "Transactional state store failed."
    )]
    private static partial void LogErrorTransactionalStateStoreFailed(ILogger logger, Exception ex);
}
