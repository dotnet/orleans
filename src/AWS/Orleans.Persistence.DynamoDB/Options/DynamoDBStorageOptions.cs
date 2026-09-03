using System;
using Orleans.Persistence.DynamoDB;
using Orleans.Runtime;
using Orleans.Storage;

namespace Orleans.Configuration
{
    /// <summary>
    /// Configures the DynamoDB grain storage provider.
    /// </summary>
    public class DynamoDBStorageOptions : DynamoDBClientOptions, IStorageProviderSerializerOptions
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
        /// Indicates if grain data should be deleted or reset to defaults when a grain clears it's state.
        /// </summary>
        public bool DeleteStateOnClear { get; set; } = false;

        /// <summary>
        /// Stage of silo lifecycle where storage should be initialized.  Storage must be initialized prior to use.
        /// </summary>
        public int InitStage { get; set; } = DEFAULT_INIT_STAGE;

        /// <summary>
        /// The default silo lifecycle stage at which the storage provider is initialized.
        /// </summary>
        public const int DEFAULT_INIT_STAGE = ServiceLifecycleStage.ApplicationServices;

        /// <summary>
        /// Specifies a time span in which the item would be expired in the future
        /// every StateWrite will increase the TTL of the grain
        /// </summary>
        public TimeSpan? TimeToLive { get; set; }

        /// <inheritdoc/>
        public IGrainStorageSerializer GrainStorageSerializer { get; set; } = null!;
    }

    /// <summary>
    /// Configuration validator for DynamoDBStorageOptions
    /// </summary>
    public class DynamoDBGrainStorageOptionsValidator : IConfigurationValidator
    {
        private readonly DynamoDBStorageOptions options;
        private readonly string name;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="options">The option to be validated.</param>
        /// <param name="name">The option name to be validated.</param>
        public DynamoDBGrainStorageOptionsValidator(DynamoDBStorageOptions options, string name)
        {
            this.options = options;
            this.name = name;
        }

        /// <inheritdoc/>
        public void ValidateConfiguration()
        {
            DynamoDBProviderConfiguration.ValidateTableOptions(
                options,
                options.TableName,
                options.UseProvisionedThroughput,
                options.ReadCapacityUnits,
                options.WriteCapacityUnits,
                $"DynamoDB grain storage provider '{name}'",
                requireService: false);
        }
    }
}
