using Orleans.Runtime;
using Orleans.Storage;
#if CLUSTERING_DYNAMODB
using Orleans.Clustering.DynamoDB;
#elif PERSISTENCE_DYNAMODB
using Orleans.Persistence.DynamoDB;
#elif REMINDERS_DYNAMODB
using Orleans.Reminders.DynamoDB;
#elif AWSUTILS_TESTS
using Orleans.AWSUtils.Tests;
#elif TRANSACTIONS_DYNAMODB
using Orleans.Transactions.DynamoDB;
#else
#endif

namespace Orleans.Configuration
{
    public class DynamoDBTransactionalStorageOptions : DynamoDBClientOptions, IStorageProviderSerializerOptions
    {
        /// <summary>
        /// Gets or sets a unique identifier for this service, which should survive deployment and redeployment.
        /// </summary>
        public string ServiceId { get; set; } = string.Empty;

        /// <summary>
        /// Use Provisioned Throughput for tables
        /// </summary>
        public bool UseProvisionedThroughput { get; set; } = true;

        /// <summary>
        /// Create the table if it doesn't exist
        /// </summary>
        public bool CreateIfNotExists { get; set; } = true;

        /// <summary>
        /// Update the table if it exists
        /// </summary>
        public bool UpdateIfExists { get; set; } = true;

        /// <summary>
        /// Read capacity unit for DynamoDB storage
        /// </summary>
        public int ReadCapacityUnits { get; set; } = DynamoDBStorage.DefaultReadCapacityUnits;

        /// <summary>
        /// Write capacity unit for DynamoDB storage
        /// </summary>
        public int WriteCapacityUnits { get; set; } = DynamoDBStorage.DefaultWriteCapacityUnits;

        /// <summary>
        /// DynamoDB table name.
        /// Defaults to 'OrleansGrainState'.
        /// </summary>
        public string TableName { get; set; } = "OrleansGrainState";

        /// <summary>
        /// Stage of silo lifecycle where storage should be initialized.  Storage must be initialized prior to use.
        /// </summary>
        public int InitStage { get; set; } = DEFAULT_INIT_STAGE;
        public const int DEFAULT_INIT_STAGE = ServiceLifecycleStage.ApplicationServices;

        public IGrainStorageSerializer GrainStorageSerializer { get; set; }
    }

    /// <summary>
    /// Configuration validator for DynamoDBTransactionalStorageOptions
    /// </summary>
    public class DynamoDBTransactionalStorageOptionsValidator : IConfigurationValidator
    {
        private readonly DynamoDBTransactionalStorageOptions options;
        private readonly string name;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="options">The option to be validated.</param>
        /// <param name="name">The option name to be validated.</param>
        public DynamoDBTransactionalStorageOptionsValidator(DynamoDBTransactionalStorageOptions options, string name)
        {
            this.options = options;
            this.name = name;
        }

        public void ValidateConfiguration()
        {
            if (string.IsNullOrWhiteSpace(this.options.TableName))
                throw new OrleansConfigurationException(
                    $"Configuration for DynamoDBGrainStorage {this.name} is invalid. {nameof(this.options.TableName)} is not valid.");

            if (this.options.UseProvisionedThroughput)
            {
                if (this.options.ReadCapacityUnits == 0)
                    throw new OrleansConfigurationException(
                        $"Configuration for DynamoDBGrainStorage {this.name} is invalid. {nameof(this.options.ReadCapacityUnits)} is not valid.");

                if (this.options.WriteCapacityUnits == 0)
                    throw new OrleansConfigurationException(
                        $"Configuration for DynamoDBGrainStorage {this.name} is invalid. {nameof(this.options.WriteCapacityUnits)} is not valid.");
            }
        }
    }
}
