namespace Orleans.Clustering.AzureStorage
{
    public class AzureStorageGatewayOptions : AzureStorageOperationOptions
    {
        public override string TableName { get; set; } = AzureStorageClusteringOptions.DEFAULT_TABLE_NAME;
    }
}