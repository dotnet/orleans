using Orleans.Reminders.DynamoDB;
using Orleans.Runtime;

namespace Orleans.Configuration
{
    /// <summary>
    /// Configuration for Amazon DynamoDB reminder storage.
    /// </summary>
    public class DynamoDBReminderStorageOptions : DynamoDBClientOptions
    {
        /// <summary>
        /// Read capacity unit for DynamoDB storage
        /// </summary>
        public int ReadCapacityUnits { get; set; } = DynamoDBStorage.DefaultReadCapacityUnits;

        /// <summary>
        /// Write capacity unit for DynamoDB storage
        /// </summary>
        public int WriteCapacityUnits { get; set; } = DynamoDBStorage.DefaultWriteCapacityUnits;

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
        /// DynamoDB table name.
        /// Defaults to 'OrleansReminders'.
        /// </summary>
        public string TableName { get; set; } = "OrleansReminders";
    }
    internal sealed class DynamoDBReminderStorageOptionsValidator(DynamoDBReminderStorageOptions options) : IConfigurationValidator
    {
        public void ValidateConfiguration()
            => DynamoDBProviderConfiguration.ValidateTableOptions(
                options,
                options.TableName,
                options.UseProvisionedThroughput,
                options.ReadCapacityUnits,
                options.WriteCapacityUnits,
                "DynamoDB reminder storage");
    }
}
