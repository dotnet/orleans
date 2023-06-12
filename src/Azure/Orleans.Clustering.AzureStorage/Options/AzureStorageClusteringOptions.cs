namespace Orleans.Clustering.AzureStorage;

/// <summary>
/// Specify options used for AzureTableBasedMembership
/// </summary>
public class AzureStorageClusteringOptions : AzureStorageOperationOptions
{
    public override string TableName { get; set; } = DEFAULT_TABLE_NAME;
    public const string DEFAULT_TABLE_NAME = "OrleansSiloInstances";
}

/// <summary>
/// Configuration validator for <see cref="AzureStorageClusteringOptions"/>.
/// </summary>
public class AzureStorageClusteringOptionsValidator : AzureStorageOperationOptionsValidator<AzureStorageClusteringOptions>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AzureStorageClusteringOptionsValidator"/> class.
    /// </summary>
    /// <param name="options">The option to be validated.</param>
    /// <param name="name">The option name to be validated.</param>
    public AzureStorageClusteringOptionsValidator(AzureStorageClusteringOptions options, string name) : base(options, name)
    {
    }
}
