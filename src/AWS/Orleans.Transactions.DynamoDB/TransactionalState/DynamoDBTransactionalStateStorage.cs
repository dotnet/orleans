using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Transactions;
using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Orleans.Persistence.DynamoDB;
using Orleans.Storage;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions.DynamoDB.TransactionalState;

internal partial class DynamoDBTransactionalStateStorage<TState> : ITransactionalStateStorage<TState> where TState : class, new()
{
    private readonly DynamoDBStorage storage;
    private readonly string tableName;
    private readonly string partitionKey;
    private readonly IGrainStorageSerializer serializer;
    private readonly ILogger<DynamoDBTransactionalStateStorage<TState>> logger;

    // Caches loaded data for this storage instance
    private KeyEntity key;
    private List<KeyValuePair<long, StateEntity>> states;

    internal DynamoDBTransactionalStateStorage(DynamoDBStorage storage, string tableName, string partitionKey, IGrainStorageSerializer serializer, ILogger<DynamoDBTransactionalStateStorage<TState>> logger)
    {
        this.storage = storage;
        this.tableName = tableName;
        this.partitionKey = partitionKey;
        this.serializer = serializer;
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

            if (key?.ETag == null)
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
                if (kvp.Value.TransactionManager.Length == 0)
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

            // TODO:
            // LogDebugLoadedPartitionKeyRows(partition, this.key.CommittedSequenceId, new(states));

            TransactionalStateMetaData metadata = this.ConvertFromStorageFormat<TransactionalStateMetaData>(this.key.Metadata);
            return new TransactionalStorageLoadResponse<TState>(this.key.ETag.ToString(), committedState, this.key.CommittedSequenceId, metadata, PrepareRecordsToRecover);
        }
        catch (Exception ex)
        {
            this.logger.LogError(ex, "Error loading transactional state for partition key {PartitionKey}", this.partitionKey);
            throw;
        }
    }

    public Task<string> Store(string expectedETag, TransactionalStateMetaData metadata, List<PendingTransactionState<TState>> statesToPrepare, long? commitUpTo,
        long? abortAfter) =>
        throw new NotImplementedException();

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
            this.logger.LogError($"Read transactional states failed {ex}.");
            throw;
        }
    }

    internal T ConvertFromStorageFormat<T>(StateEntity entity)
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

    internal T ConvertFromStorageFormat<T>(byte[] value)
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
}
