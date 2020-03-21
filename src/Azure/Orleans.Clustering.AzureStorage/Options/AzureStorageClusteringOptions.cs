namespace Orleans.Configuration
{
    /// <summary>
    /// Specify options used for AzureTableBasedMembership
    /// </summary>
    public class AzureStorageClusteringOptions
    {
        /// <summary>
        /// Retry count for Azure Table operations.
        /// </summary>
        public int MaxStorageBusyRetries { get; set; }
        /// <summary>
        /// Connection string for Azure Storage
        /// </summary>
        [RedactConnectionString]
        public string ConnectionString { get; set; }

        /// <summary>
        /// Table name for Azure Storage
        /// </summary>
        public string TableName { get; set; } = DEFAULT_TABLE_NAME;
        public const string DEFAULT_TABLE_NAME = "OrleansSiloInstances";
    }
}
