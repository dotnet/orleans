namespace Orleans.Clustering.AzureStorage;

/// <summary>
/// Configures Azure Table Storage access for client gateway discovery.
/// </summary>
public class AzureStorageGatewayOptions : AzureStorageOperationOptions
{
    /// <inheritdoc />
    public override string TableName { get; set; } = AzureStorageClusteringOptions.DEFAULT_TABLE_NAME;
}

/// <summary>
/// Configuration validator for <see cref="AzureStorageGatewayOptions"/>.
/// </summary>
public class AzureStorageGatewayOptionsValidator : AzureStorageOperationOptionsValidator<AzureStorageGatewayOptions>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AzureStorageGatewayOptionsValidator"/> class.
    /// </summary>
    /// <param name="options">The option to be validated.</param>
    /// <param name="name">The option name to be validated.</param>
    public AzureStorageGatewayOptionsValidator(AzureStorageGatewayOptions options, string name) : base(options, name)
    {
    }
}
