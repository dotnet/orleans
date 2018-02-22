namespace Orleans.Configuration
{
    public class AzureStorageGatewayOptions
    {
        /// <summary>
        /// Connection string for Azure Storage
        /// </summary>
        [RedactConnectionString]
        public string ConnectionString { get; set; }
    }
}
