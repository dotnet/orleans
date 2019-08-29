namespace Orleans.Configuration
{
    public class AzureBlobLeaseProviderOptions
    {
        [RedactConnectionString]
        public string DataConnectionString { get; set; }
        public string BlobContainerName { get; set; } = DefaultBlobContainerName;
        public const string DefaultBlobContainerName = "Leases";
    }
}
