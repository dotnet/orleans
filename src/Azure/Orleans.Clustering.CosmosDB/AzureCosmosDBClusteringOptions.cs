namespace Orleans.Clustering.CosmosDB;

public class AzureCosmosDBClusteringOptions : AzureCosmosDBOptions
{
    private const string ORLEANS_CLUSTER_CONTAINER = "OrleansCluster";

    public AzureCosmosDBClusteringOptions()
    {
        this.Container = ORLEANS_CLUSTER_CONTAINER;
    }
}
