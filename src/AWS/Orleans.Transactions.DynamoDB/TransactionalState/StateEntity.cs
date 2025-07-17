using System;
using System.Globalization;

namespace Orleans.Transactions.DynamoDB.TransactionalState;

internal class StateEntity
{
    // Row keys range from state_0000000000000001 to state_7fffffffffffffff
    public const string ROW_KEY_PREFIX = "state_";
    public const string TRANSCATION_ID_PROPERTY_NAME = nameof(TransactionId);
    public const string TRANSACTION_TIMESTAMP_PROPERTY_NAME = nameof(TransactionTimestamp);
    public const string TRANSACTION_MANAGER_PROPERTY_NAME = nameof(TransactionManager);

    public long SequenceId => long.Parse(this.RowKey.Substring(ROW_KEY_PREFIX.Length), NumberStyles.AllowHexSpecifier);

    public string TransactionId { get; set; }

    public DateTime TransactionTimestamp { get; set; }

    public string TransactionManager { get; set; }

    public string PartitionKey { get; set; }

    public string RowKey { get; set; }

    public byte[] State { get; set; }

    public DateTimeOffset? Timestamp { get; set; }

    public int? ETag { get; set; }

}
