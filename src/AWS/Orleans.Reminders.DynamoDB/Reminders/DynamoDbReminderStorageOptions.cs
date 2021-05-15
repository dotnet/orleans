using Orleans.Reminders.DynamoDB;

namespace Orleans.Configuration
{
    /// <summary>
    /// Configuration for Amazon DynamoDB reminder storage.
    /// </summary>
    public class DynamoDBReminderStorageOptions : DynamoDBClientOptions
    {
        /// <summary>
        /// DynamoDB table name.
        /// Defaults to 'OrleansReminders'.
        /// </summary>
        public string TableName { get; set; } = "OrleansReminders";
    }
}