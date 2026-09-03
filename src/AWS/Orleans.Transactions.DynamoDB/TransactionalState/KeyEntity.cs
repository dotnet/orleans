using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using Amazon.DynamoDBv2.Model;

[assembly: InternalsVisibleTo("Orleans.Transactions.DynamoDB.Tests")]

namespace Orleans.Transactions.DynamoDB.TransactionalState;

internal class KeyEntity
{
    private const string COMITTED_SEQUENCE_ID_PROPERTY_NAME = nameof(CommittedSequenceId);
    private const string METADATA_PROPERTY_NAME = nameof(Metadata);

    internal KeyEntity(Dictionary<string, AttributeValue> fields)
    {
        this.RowKey = RK;

        if (fields.TryGetValue(DynamoDBTransactionalStateConstants.PARTITION_KEY_PROPERTY_NAME, out var partitionKey))
            this.PartitionKey = partitionKey.S;

        if (fields.TryGetValue(COMITTED_SEQUENCE_ID_PROPERTY_NAME, out var committedSequenceId))
            this.CommittedSequenceId = long.Parse(committedSequenceId.N, NumberStyles.Integer, CultureInfo.InvariantCulture);

        if (fields.TryGetValue(METADATA_PROPERTY_NAME, out var metadata))
            this.Metadata = metadata.B.ToArray();

        if (fields.TryGetValue(DynamoDBTransactionalStateConstants.TIMESTAMP_PROPERTY_NAME, out var timestamp))
            this.Timestamp = DateTimeOffset.FromUnixTimeSeconds(long.Parse(timestamp.N, NumberStyles.Integer, CultureInfo.InvariantCulture));

        if (fields.TryGetValue(DynamoDBTransactionalStateConstants.ETAG_PROPERTY_NAME, out var etag))
            this.ETag = long.Parse(etag.N, NumberStyles.Integer, CultureInfo.InvariantCulture);
    }

    public KeyEntity(string partitionKey)
    {
        this.PartitionKey = partitionKey;
        this.RowKey = RK;
    }

    public const string RK = "key";

    public string PartitionKey { get; set; } = null!;

    public string RowKey { get; set; } = RK;

    public long CommittedSequenceId { get; set; }

    public byte[] Metadata { get; set; } = [];

    public DateTimeOffset Timestamp { get; set; }

    public long? ETag { get; set; }

    public Dictionary<string, AttributeValue> ToStorageFormat()
    {
        var item = new Dictionary<string, AttributeValue>
        {
            { DynamoDBTransactionalStateConstants.PARTITION_KEY_PROPERTY_NAME, new AttributeValue { S = this.PartitionKey } },
            { DynamoDBTransactionalStateConstants.ROW_KEY_PROPERTY_NAME, new AttributeValue { S = this.RowKey } },
            { COMITTED_SEQUENCE_ID_PROPERTY_NAME, new AttributeValue { N = this.CommittedSequenceId.ToString(CultureInfo.InvariantCulture) } },
        };

        if (this.Metadata is { Length: > 0 })
        {
            item[METADATA_PROPERTY_NAME] = new AttributeValue { B = new MemoryStream(this.Metadata) };
        }

        if (this.Timestamp != default)
        {
            item[DynamoDBTransactionalStateConstants.TIMESTAMP_PROPERTY_NAME] = new AttributeValue { N = this.Timestamp.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) };
        }

        if (this.ETag.HasValue)
        {
            item[DynamoDBTransactionalStateConstants.ETAG_PROPERTY_NAME] = new AttributeValue { N = this.ETag.Value.ToString(CultureInfo.InvariantCulture) };
        }

        return item;
    }
}
