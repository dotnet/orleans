namespace Orleans.Clustering.Cosmos;

/// <summary>
/// Options for configuring Azure Cosmos DB clustering.
/// </summary>
public class CosmosClusteringOptions : CosmosOptions
{
    private const string ORLEANS_CLUSTER_CONTAINER = "OrleansCluster";

    /// <summary>
    /// Initializes a new <see cref="CosmosClusteringOptions"/> instance.
    /// </summary>
    public CosmosClusteringOptions()
    {
        ContainerName = ORLEANS_CLUSTER_CONTAINER;
    }
}
