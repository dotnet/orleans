namespace Orleans.Clustering.AzureCosmos;

/// <summary>
/// Options for configuring Azure Cosmos DB clustering.
/// </summary>
public class AzureCosmosClusteringOptions : AzureCosmosOptions
{
    private const string ORLEANS_CLUSTER_CONTAINER = "OrleansCluster";

    /// <summary>
    /// Initializes a new <see cref="AzureCosmosClusteringOptions"/> instance.
    /// </summary>
    public AzureCosmosClusteringOptions()
    {
        ContainerName = ORLEANS_CLUSTER_CONTAINER;
    }
}
