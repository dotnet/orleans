using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Amazon.DynamoDBv2.Model;
using Orleans.Configuration;
using Orleans.Storage;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions.DynamoDB.TransactionalState;

internal class StateEntity
{
    // Row keys range from state_0000000000000001 to state_7fffffffffffffff
    public const string ROW_KEY_PREFIX = "state_";
    public const string ROW_KEY_MIN = ROW_KEY_PREFIX;
    public const string ROW_KEY_MAX = ROW_KEY_PREFIX + "~";
    public const string TRANSACTION_ID_PROPERTY_NAME = nameof(TransactionId);
    public const string TRANSACTION_TIMESTAMP_PROPERTY_NAME = nameof(TransactionTimestamp);
    public const string TRANSACTION_MANAGER_PROPERTY_NAME = nameof(TransactionManager);

    internal StateEntity()
    {
    }

    internal StateEntity(Dictionary<string, AttributeValue> fields)
    {
        if (fields.TryGetValue(DynamoDBTransactionalStateConstants.PARTITION_KEY_PROPERTY_NAME, out var partitionKey))
            this.PartitionKey = partitionKey.S;

        if (fields.TryGetValue(DynamoDBTransactionalStateConstants.ROW_KEY_PROPERTY_NAME, out var rowKey))
            this.RowKey = rowKey.S;

        if (fields.TryGetValue(TRANSACTION_ID_PROPERTY_NAME, out var transactionId))
            this.TransactionId = transactionId.S;

        if (fields.TryGetValue(TRANSACTION_TIMESTAMP_PROPERTY_NAME, out var timestamp))
            this.TransactionTimestamp = DateTime.Parse(timestamp.S, null,DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

        if (fields.TryGetValue(TRANSACTION_MANAGER_PROPERTY_NAME, out var transactionManager))
            this.TransactionManager = transactionManager.B.ToArray();

        if (fields.TryGetValue(DynamoDBTransactionalStateConstants.BINARY_STATE_PROPERTY_NAME, out var state))
            this.State = state.B.ToArray();

        if (fields.TryGetValue(DynamoDBTransactionalStateConstants.ETAG_PROPERTY_NAME, out var etag))
            this.ETag = int.Parse(etag.N);
    }

    public static string MakeRowKey(long sequenceId)
    {
        return $"{ROW_KEY_PREFIX}{sequenceId.ToString("x16")}";
    }

    public static StateEntity Create<TState>(
        IGrainStorageSerializer serializer,
        string partitionKey,
        PendingTransactionState<TState> pendingState) where TState : class, new()
    {
        var result = new StateEntity
        {
            PartitionKey = partitionKey,
            RowKey = MakeRowKey(pendingState.SequenceId),
            TransactionId = pendingState.TransactionId,
            TransactionTimestamp = pendingState.TimeStamp,
            TransactionManager = serializer.Serialize(pendingState.TransactionManager).ToArray(),
            ETag = 0,
        };

        result.SetState(pendingState.State, serializer);
        return result;
    }

    public string PartitionKey { get; set; }

    public string RowKey { get; set; }

    public long SequenceId => long.Parse(this.RowKey.Substring(ROW_KEY_PREFIX.Length), NumberStyles.AllowHexSpecifier);

    public string TransactionId { get; set; }

    public DateTime TransactionTimestamp { get; set; }

    public byte[] TransactionManager { get; set; } = [];

    public byte[] State { get; set; }

    public long? ETag { get; set; }

    public void SetState<TState>(TState state, IGrainStorageSerializer serializer) where TState : class, new()
    {
        this.State = state == null ? null : serializer.Serialize(state).ToArray();
    }

    public Dictionary<string, AttributeValue> ToStorageFormat()
    {
        var item = new Dictionary<string, AttributeValue>
        {
            { DynamoDBTransactionalStateConstants.PARTITION_KEY_PROPERTY_NAME, new AttributeValue { S = this.PartitionKey } },
            { DynamoDBTransactionalStateConstants.ROW_KEY_PROPERTY_NAME, new AttributeValue { S = this.RowKey } },
        };

        if (this.State is { Length: > 0 })
            item[DynamoDBTransactionalStateConstants.BINARY_STATE_PROPERTY_NAME] = new AttributeValue { B = new MemoryStream(this.State) };

        if (!string.IsNullOrEmpty(this.TransactionId))
            item[TRANSACTION_ID_PROPERTY_NAME] = new AttributeValue { S = this.TransactionId };

        if (this.TransactionTimestamp != default)
            item[TRANSACTION_TIMESTAMP_PROPERTY_NAME] = new AttributeValue { S = this.TransactionTimestamp.ToUniversalTime().ToString("o") };

        if (this.TransactionManager is { Length: > 0 })
            item[TRANSACTION_MANAGER_PROPERTY_NAME] = new AttributeValue { B = new MemoryStream(this.TransactionManager) };

        if (this.ETag.HasValue)
            item[DynamoDBTransactionalStateConstants.ETAG_PROPERTY_NAME] = new AttributeValue { N = this.ETag.Value.ToString() };

        return item;
    }
}
