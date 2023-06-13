namespace Orleans.Clustering.AzureCosmos;

public class AzureCosmosClusteringOptions : AzureCosmosOptions
{
    private const string ORLEANS_CLUSTER_CONTAINER = "OrleansCluster";

    public AzureCosmosClusteringOptions()
    {
        ContainerName = ORLEANS_CLUSTER_CONTAINER;
    }
}
