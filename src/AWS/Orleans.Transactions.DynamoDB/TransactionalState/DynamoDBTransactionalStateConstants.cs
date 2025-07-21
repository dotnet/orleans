namespace Orleans.Transactions.DynamoDB.TransactionalState;

public class DynamoDBTransactionalStateConstants
{
    public const string PARTITION_KEY_PROPERTY_NAME = "PartitionKey";

    public const string ROW_KEY_PROPERTY_NAME = "RowKey";

    public const string BINARY_STATE_PROPERTY_NAME = "GrainState";

    public const string ETAG_PROPERTY_NAME = "ETag";

    public const string TIMESTAMP_PROPERTY_NAME = "Timestamp";

    public const string CURRENT_ETAG_ALIAS = ":currentETag";
}
