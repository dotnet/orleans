using System.Text.Json;
using Azure;
using Azure.Core;
using Microsoft.Extensions.DependencyInjection;

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
/// Options for Azure Cosmos DB storage.
/// </summary>
public abstract class CosmosOptions
{
    /// <summary>
    /// Tries to create the database and container used for clustering if it does not exist.
    /// </summary>
    public bool IsResourceCreationEnabled { get; set; }

    /// <summary>
    /// Database configured throughput. If set to <see langword="null"/>, which is the default value, it will not be configured. 
    /// </summary>
    /// <seealso href="https://docs.microsoft.com/en-us/azure/cosmos-db/set-throughput"/>
    public int? DatabaseThroughput { get; set; }

    /// <summary>
    /// The name of the database to use for clustering information.
    /// </summary>
    public string DatabaseName { get; set; } = "Orleans";

    /// <summary>
    /// The name of the container to use to store clustering information.
    /// </summary>
    public string ContainerName { get; set; } = default!;

    /// <summary>
    /// Throughput properties for containers. The default value is <see langword="null"/>, which indicates that the serverless throughput mode will be used.
    /// </summary>
    /// <seealso href="https://docs.microsoft.com/en-us/azure/cosmos-db/set-throughput"/>
    public ThroughputProperties? ContainerThroughputProperties { get; set; }

    /// <summary>
    /// Delete the database on initialization. Intended only for testing scenarios.
    /// </summary>
    /// <remarks>This is only intended for use in testing scenarios.</remarks>
    public bool CleanResourcesOnInitialization { get; set; }

    /// <summary>
    /// The options passed to the Cosmos DB client, or <see langword="null"/> to use default options.
    /// </summary>
    public CosmosClientOptions? ClientOptions { get; set; }

    /// <summary>
    /// Configures the Cosmos DB client.
    /// </summary>
    /// <param name="connectionString">The connection string.</param>
    /// <see cref="CosmosClient(string, CosmosClientOptions)"/>
    public void ConfigureCosmosClient(string connectionString)
    {
        CreateClient = _ => new(new CosmosClient(connectionString, ClientOptions));
    }

    /// <summary>
    /// Configures the Cosmos DB client.
    /// </summary>
    /// <param name="accountEndpoint">The account endpoint. In the form of <code>https://{databaseaccount}.documents.azure.com:443/</code>, <see href="https://docs.microsoft.com/en-us/rest/api/cosmos-db/cosmosdb-resource-uri-syntax-for-rest"/></param>
    /// <param name="authKeyOrResourceTokenCredential"><see cref="AzureKeyCredential"/> with master-key or resource token.</param>
    /// <see cref="CosmosClient(string, AzureKeyCredential, CosmosClientOptions)"/>
    public void ConfigureCosmosClient(string accountEndpoint, AzureKeyCredential authKeyOrResourceTokenCredential)
    {
        CreateClient = _ => new(new CosmosClient(accountEndpoint, authKeyOrResourceTokenCredential, ClientOptions));
    }

    /// <summary>
    /// Configures the Cosmos DB client.
    /// </summary>
    /// <param name="accountEndpoint">The account endpoint. In the form of <code>https://{databaseaccount}.documents.azure.com:443/</code>, <see href="https://docs.microsoft.com/en-us/rest/api/cosmos-db/cosmosdb-resource-uri-syntax-for-rest"/></param>
    /// <param name="tokenCredential">The token to provide AAD for authorization.</param>
    /// <see cref="CosmosClient(string, TokenCredential, CosmosClientOptions)"/>
    public void ConfigureCosmosClient(string accountEndpoint, TokenCredential tokenCredential)
    {
        CreateClient = _ => new(new CosmosClient(accountEndpoint, tokenCredential, ClientOptions));
    }

    /// <summary>
    /// Configures the Cosmos DB client.
    /// </summary>
    /// <param name="accountEndpoint">The account endpoint. In the form of <code>https://{databaseaccount}.documents.azure.com:443/</code>, <see href="https://docs.microsoft.com/en-us/rest/api/cosmos-db/cosmosdb-resource-uri-syntax-for-rest"/></param>
    /// <param name="authKeyOrResourceToken">The Cosmos account key or resource token to use to create the client.</param>
    /// <see cref="CosmosClient(string, TokenCredential, CosmosClientOptions)"/>
    public void ConfigureCosmosClient(string accountEndpoint, string authKeyOrResourceToken)
    {
        CreateClient = _ => new(new CosmosClient(accountEndpoint, authKeyOrResourceToken, ClientOptions));
    }

    /// <summary>
    /// Configures the Cosmos DB client.
    /// </summary>
    /// <param name="createClient">The delegate used to create the Cosmos DB client.</param>
    public void ConfigureCosmosClient(Func<IServiceProvider, ValueTask<CosmosClient>> createClient)
    {
        CreateClient = createClient ?? throw new ArgumentNullException(nameof(createClient));
    }

    /// <summary>
    /// Factory method for creating a <see cref="CosmosClient"/>.
    /// </summary>
    internal Func<IServiceProvider, ValueTask<CosmosClient>> CreateClient { get; private set; } = null!;
}
