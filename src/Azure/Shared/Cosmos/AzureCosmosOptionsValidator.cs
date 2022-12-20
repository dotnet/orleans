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

public class AzureCosmosOptionsValidator<TOptions> : IConfigurationValidator where TOptions : AzureCosmosOptions
{
    private readonly TOptions _options;
    private readonly string _name;

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="options">The option to be validated.</param>
    /// <param name="name">The option name to be validated.</param>
    public AzureCosmosOptionsValidator(TOptions options, string name)
    {
        _options = options;
        _name = name;
    }

    public void ValidateConfiguration()
    {
        if (string.IsNullOrWhiteSpace(_options.Database))
            throw new OrleansConfigurationException(
                $"Configuration for Azure Cosmos DB provider {_name} is invalid. {nameof(_options.Database)} is not valid.");

        if (string.IsNullOrWhiteSpace(_options.Container))
            throw new OrleansConfigurationException(
                $"Configuration for Azure Cosmos DB provider {_name} is invalid. {nameof(_options.Container)} is not valid.");

        if (_options.ContainerThroughput < 400 && _options.DatabaseThroughput < 400)
            throw new OrleansConfigurationException(
                $"Configuration for Azure Cosmos DB provider {_name} is invalid. Either {nameof(_options.ContainerThroughput)} or {nameof(_options.DatabaseThroughput)} must exceed 400.");

        if (_options.CreateClient is null)
        {
            throw new OrleansConfigurationException(
                $"Configuration for Azure Cosmos DB provider {_name} is invalid. You must call {nameof(_options.ConfigureCosmosClient)} to configure access to Azure Cosmos DB.");
        }
    }
}

public class AzureCosmosPostConfigureOptions<TOptions> : IPostConfigureOptions<TOptions> where TOptions : AzureCosmosOptions
{
    private readonly IServiceProvider _serviceProvider;

    public AzureCosmosPostConfigureOptions(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public void PostConfigure(string? name, TOptions options)
    {
        options.ClientOptions ??= options.CreateDefaultOptions(_serviceProvider);
    }
}