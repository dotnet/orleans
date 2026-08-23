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

    /// <summary>
    /// Gets or sets the name of the companion container used to store immutable silo metadata.
    /// When unset, a deterministic name is derived from <see cref="CosmosOptions.ContainerName"/>.
    /// </summary>
    /// <remarks>
    /// The companion container uses <c>/ClusterId</c> as its partition key. When resource creation
    /// is disabled, provision this container together with the membership container. The two
    /// container names must differ.
    /// </remarks>
    public string? MetadataContainerName { get; set; }
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
