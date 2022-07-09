#if ORLEANS_CLUSTERING
namespace Orleans.Clustering.CosmosDB;
#elif ORLEANS_PERSISTENCE
namespace Orleans.Persistence.CosmosDB;
#elif ORLEANS_REMINDERS
namespace Orleans.Reminders.CosmosDB;
#elif ORLEANS_STREAMING
namespace Orleans.Streaming.CosmosDB;
#elif ORLEANS_DIRECTORY
namespace Orleans.GrainDirectory.CosmosDB;
#else
// No default namespace intentionally to cause compile errors if something is not defined
#endif

public class AzureCosmosDBOptionsValidator<TOptions> : IConfigurationValidator where TOptions : AzureCosmosDBOptions
{
    private readonly TOptions _options;
    private readonly string _name;

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="options">The option to be validated.</param>
    /// <param name="name">The option name to be validated.</param>
    public AzureCosmosDBOptionsValidator(TOptions options, string name)
    {
        this._options = options;
        this._name = name;
    }

    public void ValidateConfiguration()
    {
        if (string.IsNullOrWhiteSpace(this._options.Database))
            throw new OrleansConfigurationException(
                $"Configuration for Azure CosmosDB provider {this._name} is invalid. {nameof(this._options.Database)} is not valid.");

        if (string.IsNullOrWhiteSpace(this._options.Container))
            throw new OrleansConfigurationException(
                $"Configuration for Azure CosmosDB provider {this._name} is invalid. {nameof(this._options.Container)} is not valid.");

        if (this._options.ContainerThroughput < 400 && this._options.DatabaseThroughput < 400)
            throw new OrleansConfigurationException(
                $"Configuration for Azure CosmosDB provider {this._name} is invalid. Either {nameof(this._options.ContainerThroughput)} or {nameof(this._options.DatabaseThroughput)} must exceed 400.");

        if (this._options.CosmosDBClientFactory is not null) return;

        if(string.IsNullOrWhiteSpace(this._options.AccountEndpoint))
            throw new OrleansConfigurationException(
                $"Configuration for Azure CosmosDB provider {this._name} is invalid. {nameof(this._options.AccountEndpoint)} is not valid.");
        
        if(string.IsNullOrWhiteSpace(this._options.AccountKey) && this._options.Credential is null)
            throw new OrleansConfigurationException(
                $"Configuration for Azure CosmosDB provider {this._name} is invalid. {nameof(this._options.AccountKey)} or {nameof(this._options.Credential)} must be set.");        
    }
}