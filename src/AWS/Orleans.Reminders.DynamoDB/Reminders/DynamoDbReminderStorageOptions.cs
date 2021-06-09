using Orleans.Reminders.DynamoDB;

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
        /// DynamoDB table name.
        /// Defaults to 'OrleansReminders'.
        /// </summary>
        public string TableName { get; set; } = "OrleansReminders";
    }
}