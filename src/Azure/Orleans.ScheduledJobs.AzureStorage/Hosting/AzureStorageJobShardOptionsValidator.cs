using Microsoft.Extensions.Options;
using Orleans.Configuration.Internal;
using Orleans.Runtime;

namespace Orleans.Hosting;

/// <summary>
/// Validates <see cref="AzureStorageJobShardOptions"/>.
/// </summary>
public class AzureStorageJobShardOptionsValidator : IConfigurationValidator
{
    private readonly AzureStorageJobShardOptions _options;
    private readonly string _name;

    /// <summary>
    /// Initializes a new instance of the <see cref="AzureStorageJobShardOptionsValidator"/> class.
    /// </summary>
    /// <param name="options">The options.</param>
    /// <param name="name">The name.</param>
    public AzureStorageJobShardOptionsValidator(AzureStorageJobShardOptions options, string name)
    {
        _options = options;
        _name = name;
    }

    /// <inheritdoc/>
    public void ValidateConfiguration()
    {
        if (_options.BlobServiceClient is null)
        {
            throw new OrleansConfigurationException($"Invalid configuration for {nameof(AzureStorageJobShardOptions)} with name '{_name}'. {nameof(_options.BlobServiceClient)} is required.");
        }

        if (string.IsNullOrWhiteSpace(_options.ContainerName))
        {
            throw new OrleansConfigurationException($"Invalid configuration for {nameof(AzureStorageJobShardOptions)} with name '{_name}'. {nameof(_options.ContainerName)} is required.");
        }
    }
}
