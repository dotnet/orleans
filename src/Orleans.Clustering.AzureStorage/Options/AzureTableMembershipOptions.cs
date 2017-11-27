namespace Orleans.AzureUtils.Configuration
{
    /// <summary>
    /// Specify options used for AzureTableBasedMembership
    /// </summary>
    public class AzureTableMembershipOptions
    {
        /// <summary>
        /// Retry count for Azure Table operations. 
        /// </summary>
        public int MaxStorageBusyRetries { get; set; }
        /// <summary>
        /// Connection string for Azure Storage
        /// </summary>
        public string ConnectionString { get; set; }
    }
}
