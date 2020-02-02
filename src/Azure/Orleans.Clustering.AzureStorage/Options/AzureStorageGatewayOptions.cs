namespace Orleans.Configuration
{
    public class AzureStorageGatewayOptions
    {
        /// <summary>
        /// Connection string for Azure Storage
        /// </summary>
        [RedactConnectionString]
        public string ConnectionString { get; set; }

        /// <summary>
        /// Table name for Azure Storage
        /// </summary>
        public string TableName { get; set; } = AzureStorageClusteringOptions.DEFAULT_TABLE_NAME;
    }
}
