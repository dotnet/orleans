namespace Orleans.Transactions.DynamoDB.TransactionalState;

public class DynamoDBTransactionalStateConstants
{
    public const int MAX_DATA_SIZE = 400 * 1024;
    public const string GRAIN_REFERENCE_PROPERTY_NAME = "GrainReference";
    public const string BINARY_STATE_PROPERTY_NAME = "GrainState";
    public const string GRAIN_TYPE_PROPERTY_NAME = "GrainType";
    public const string ETAG_PROPERTY_NAME = "ETag";
    public const string CURRENT_ETAG_ALIAS = ":currentETag";
}
