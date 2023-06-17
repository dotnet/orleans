#if ORLEANS_CLUSTERING
namespace Orleans.Clustering.Cosmos;
#elif ORLEANS_PERSISTENCE
namespace Orleans.Persistence.Cosmos;
#elif ORLEANS_REMINDERS
namespace Orleans.Reminders.Cosmos;
#elif ORLEANS_STREAMING
namespace Orleans.Streaming.Cosmos;
#elif ORLEANS_DIRECTORY
namespace Orleans.GrainDirectory.Cosmos;
#else
// No default namespace intentionally to cause compile errors if something is not defined
#endif

/// <summary>
/// Validates instances of <see cref="CosmosOptions"/>.
/// </summary>
/// <typeparam name="TOptions">The options type.</typeparam>
public class CosmosOptionsValidator<TOptions> : IConfigurationValidator where TOptions : CosmosOptions
{
    private readonly TOptions _options;
    private readonly string _name;

    /// <summary>
    /// Initializes a new instance of the <see cref="CosmosOptionsValidator{TOptions}"/> type.
    /// </summary>
    /// <param name="options">The instance to be validated.</param>
    /// <param name="name">The option name to be validated.</param>
    public CosmosOptionsValidator(TOptions options, string name)
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