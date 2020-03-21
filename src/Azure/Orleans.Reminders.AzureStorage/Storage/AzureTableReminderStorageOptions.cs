namespace Orleans.Configuration
{
    /// <summary>Options for Azure Table based reminder table.</summary>
    public class AzureTableReminderStorageOptions
    {
        /// <summary>
        /// Gets or sets the storage connection string.
        /// </summary>
        [RedactConnectionString]
        public string ConnectionString { get; set; }

        /// <summary>
        /// Table name for Azure Storage
        /// </summary>
        public string TableName { get; set; } = DEFAULT_TABLE_NAME;
        public const string DEFAULT_TABLE_NAME = "OrleansReminders";
    }
}
