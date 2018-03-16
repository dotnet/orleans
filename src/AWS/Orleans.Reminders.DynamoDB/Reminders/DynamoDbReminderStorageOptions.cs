using Orleans.Reminders.DynamoDB;

namespace Orleans.Configuration
{
    /// <summary>
    /// Configuration for Amazon DynamoDB reminder storage.
    /// </summary>
    public class DynamoDBReminderStorageOptions
    {
        /// <summary>
        /// AccessKey string for DynamoDB Storage
        /// </summary>
        [Redact]
        public string AccessKey { get; set; }

        /// <summary>
        /// Secret key for DynamoDB storage
        /// </summary>
        [Redact]
        public string SecretKey { get; set; }

        /// <summary>
        /// DynamoDB Service name 
        /// </summary>
        public string Service { get; set; }

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