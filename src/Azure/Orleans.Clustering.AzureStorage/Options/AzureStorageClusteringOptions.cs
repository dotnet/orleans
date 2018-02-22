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
    }
}
