namespace Orleans.Transactions.DynamoDB.TransactionalState;

/// <summary>
/// Constants used by DynamoDB transactional state storage.
/// </summary>
public static class DynamoDBTransactionalStateConstants
{
    /// <summary>
    /// The DynamoDB partition key attribute name.
    /// </summary>
    public const string PARTITION_KEY_PROPERTY_NAME = "PartitionKey";

    /// <summary>
    /// The DynamoDB row key attribute name.
    /// </summary>
    public const string ROW_KEY_PROPERTY_NAME = "RowKey";

    /// <summary>
    /// The serialized grain state attribute name.
    /// </summary>
    public const string BINARY_STATE_PROPERTY_NAME = "GrainState";

    /// <summary>
    /// The entity tag attribute name.
    /// </summary>
    public const string ETAG_PROPERTY_NAME = "ETag";

    /// <summary>
    /// The timestamp attribute name.
    /// </summary>
    public const string TIMESTAMP_PROPERTY_NAME = "Timestamp";

    /// <summary>
    /// The expression alias for the current entity tag.
    /// </summary>
    public const string CURRENT_ETAG_ALIAS = ":currentETag";
}
