using System;

namespace Orleans.Transactions.DynamoDB.TransactionalState;

internal class KeyEntity
{
    public const string RK = "key";

    public long CommittedSequenceId { get; set; }

    public string Metadata { get; set; }

    public string PartitionKey { get; set; }

    public string RowKey { get; set; }

    public DateTimeOffset? Timestamp { get; set; }

    public int? ETag { get; set; }
}
