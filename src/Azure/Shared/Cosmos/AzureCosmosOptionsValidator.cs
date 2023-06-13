#if ORLEANS_CLUSTERING
namespace Orleans.Clustering.AzureCosmos;
#elif ORLEANS_PERSISTENCE
namespace Orleans.Persistence.AzureCosmos;
#elif ORLEANS_REMINDERS
namespace Orleans.Reminders.AzureCosmos;
#elif ORLEANS_STREAMING
namespace Orleans.Streaming.AzureCosmos;
#elif ORLEANS_DIRECTORY
namespace Orleans.GrainDirectory.AzureCosmos;
#else
// No default namespace intentionally to cause compile errors if something is not defined
#endif

/// <summary>
/// Validates instances of <see cref="AzureCosmosOptions"/>.
/// </summary>
/// <typeparam name="TOptions">The options type.</typeparam>
public class AzureCosmosOptionsValidator<TOptions> : IConfigurationValidator where TOptions : AzureCosmosOptions
{
    private readonly TOptions _options;
    private readonly string _name;

    /// <summary>
    /// Initializes a new instance of the <see cref="AzureCosmosOptionsValidator{TOptions}"/> type.
    /// </summary>
    /// <param name="options">The instance to be validated.</param>
    /// <param name="name">The option name to be validated.</param>
    public AzureCosmosOptionsValidator(TOptions options, string name)
    {
        _options = options;
        _name = name;
    }

    /// <inheritdoc/>
    public void ValidateConfiguration()
    {
        if (string.IsNullOrWhiteSpace(_options.DatabaseName))
            throw new OrleansConfigurationException(
                $"Configuration for Azure Cosmos DB provider {_name} is invalid. {nameof(_options.DatabaseName)} is not valid.");

        if (string.IsNullOrWhiteSpace(_options.ContainerName))
            throw new OrleansConfigurationException(
                $"Configuration for Azure Cosmos DB provider {_name} is invalid. {nameof(_options.ContainerName)} is not valid.");

        if (_options.CreateClient is null)
        {
            throw new OrleansConfigurationException(
                $"Configuration for Azure Cosmos DB provider {_name} is invalid. You must call {nameof(_options.ConfigureCosmosClient)} to configure access to Azure Cosmos DB.");
        }
    }
}