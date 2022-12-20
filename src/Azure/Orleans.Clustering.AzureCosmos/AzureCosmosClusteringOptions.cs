namespace Orleans.Clustering.AzureCosmos;

public class AzureCosmosClusteringOptions : AzureCosmosOptions
{
    private const string ORLEANS_CLUSTER_CONTAINER = "OrleansCluster";

    public AzureCosmosClusteringOptions()
    {
        Container = ORLEANS_CLUSTER_CONTAINER;
    }
}
