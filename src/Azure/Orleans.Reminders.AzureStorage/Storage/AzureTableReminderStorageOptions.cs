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
    }
}