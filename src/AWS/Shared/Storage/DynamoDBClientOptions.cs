#if CLUSTERING_DYNAMODB
namespace Orleans.Clustering.DynamoDB
#elif PERSISTENCE_DYNAMODB
namespace Orleans.Persistence.DynamoDB
#elif REMINDERS_DYNAMODB
namespace Orleans.Reminders.DynamoDB
#elif AWSUTILS_TESTS
namespace Orleans.AWSUtils.Tests
#elif TRANSACTIONS_DYNAMODB
namespace Orleans.Transactions.DynamoDB
#else
// No default namespace intentionally to cause compile errors if something is not defined
#endif
{
    /// <summary>
    ///     Options for configuring the DynamoDB behaviour
    /// </summary>
    public class DynamoDBClientOptions
    {
        /// <summary>
        ///     The DynamoDB implementation to use. Can either be the service url, eg: http://localstack:4566 or a region endpoint, eg: eu-west-1.
        /// </summary>
        public string Service { get; set; }
        /// <summary>
        ///     The access key of the IAM principal to use. Must be used with <see cref="SecretKey"/>
        /// </summary>
        [Redact]
        public string AccessKey { get; set; } = "";
        /// <summary>
        ///     The secret key of the IAM principal to use. Must be used with <see cref="AccessKey"/>
        /// </summary>
        [Redact]
        public string SecretKey { get; set; } = "";
        /// <summary>
        ///     If a table is uninitialized and <see cref="UseProvisionedThroughput"/> is set to true, then the table will have its ReadCapacityUnits set to this value. Defaults to: 10
        /// </summary>
        public int ReadCapacityUnits { get; set; } = 10;
        /// <summary>
        ///     If a table is uninitialized and <see cref="UseProvisionedThroughput"/> is set to true, then the table will have its WriteCapacityUnits set to this value. Defaults to: 5
        /// </summary>
        public int WriteCapacityUnits { get; set; } = 5;
        /// <summary>
        ///     If a table is uninitialized, the table will be initialized with provisioned throughput guided by <see cref="ReadCapacityUnits"/> and <see cref="WriteCapacityUnits"/>, otherwise the PayPerRequest model is used for table initialization.
        /// </summary>
        public bool UseProvisionedThroughput { get; set; } = true;
    }
}