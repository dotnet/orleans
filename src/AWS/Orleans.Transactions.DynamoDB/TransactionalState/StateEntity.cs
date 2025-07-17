using System;
using System.Collections.Generic;
using System.Globalization;
using Amazon.DynamoDBv2.Model;

namespace Orleans.Transactions.DynamoDB.TransactionalState;

internal class StateEntity
{
    // Row keys range from state_0000000000000001 to state_7fffffffffffffff
    public const string ROW_KEY_PREFIX = "state_";
    public const string ROW_KEY_MIN = ROW_KEY_PREFIX;
    public const string ROW_KEY_MAX = ROW_KEY_PREFIX + "~";
    public const string TRANSCATION_ID_PROPERTY_NAME = nameof(TransactionId);
    public const string TRANSACTION_TIMESTAMP_PROPERTY_NAME = nameof(TransactionTimestamp);
    public const string TRANSACTION_MANAGER_PROPERTY_NAME = nameof(TransactionManager);

    internal StateEntity(Dictionary<string, AttributeValue> fields)
    {
        if (fields.TryGetValue(DynamoDBTransactionalStateConstants.PARTITION_KEY_PROPERTY_NAME, out var partitionKey))
            this.PartitionKey = partitionKey.S;

        if (fields.TryGetValue(DynamoDBTransactionalStateConstants.ROW_KEY_PROPERTY_NAME, out var rowKey))
            this.RowKey = rowKey.S;

        if (fields.TryGetValue(TRANSCATION_ID_PROPERTY_NAME, out var transactionId))
            this.TransactionId = transactionId.S;

        if (fields.TryGetValue(TRANSACTION_TIMESTAMP_PROPERTY_NAME, out var timestamp))
            this.TransactionTimestamp = DateTimeOffset.FromUnixTimeSeconds(long.Parse(timestamp.N)).UtcDateTime;

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

    public string PartitionKey { get; set; }

    public string RowKey { get; set; }

    public long SequenceId => long.Parse(this.RowKey.Substring(ROW_KEY_PREFIX.Length), NumberStyles.AllowHexSpecifier);

    public string TransactionId { get; set; }

    public DateTime TransactionTimestamp { get; set; }

    public byte[] TransactionManager { get; set; }

    public byte[] State { get; set; }

    public int? ETag { get; set; }

}
