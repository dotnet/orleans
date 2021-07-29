using Orleans.AzureCosmos;

namespace Orleans.Configuration
{
    /// <summary>
    /// Specify options used for Azure Cosmos DB clustering storage
    /// </summary>
    public class AzureCosmosClusteringOptions : StorageOptionsBase
    {
        public AzureCosmosClusteringOptions() => ContainerName = DEFAULT_CONTAINER_NAME;
        public const string DEFAULT_CONTAINER_NAME = "OrleansSiloInstances";
    }
}
