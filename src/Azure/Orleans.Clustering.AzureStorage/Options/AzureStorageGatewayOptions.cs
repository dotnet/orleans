using System.ComponentModel;

namespace Orleans.Clustering.AzureStorage
{
    public class AzureStorageGatewayOptions : AzureStorageOperationOptions
    {
        [DefaultValue("OrleansGateway")]
        public override string TableName { get; set; }
    }
}