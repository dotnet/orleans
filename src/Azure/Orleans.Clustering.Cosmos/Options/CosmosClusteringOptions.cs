namespace Orleans.Clustering.Cosmos;

/// <summary>
/// Options for configuring Azure Cosmos DB clustering.
/// </summary>
/// <remarks>
/// Membership uses single-partition transactions and Strong reads with table-version fencing.
/// Configure the account with a single writable region and Strong consistency.
/// </remarks>
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

/// <summary>
/// Configuration validator for <see cref="CosmosClusteringOptions"/>.
/// </summary>
public class CosmosClusteringOptionsValidator : CosmosOptionsValidator<CosmosClusteringOptions>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CosmosClusteringOptionsValidator"/> class.
    /// </summary>
    /// <param name="options">The option to be validated.</param>
    /// <param name="name">The option name to be validated.</param>
    public CosmosClusteringOptionsValidator(CosmosClusteringOptions options, string name) : base(options, name)
    {
    }
}
