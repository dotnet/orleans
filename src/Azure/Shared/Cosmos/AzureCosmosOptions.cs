using System.Text.Json;
using Azure;
using Azure.Core;
using Microsoft.Extensions.DependencyInjection;

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

public abstract class AzureCosmosOptions
{
    private const string ORLEANS_DB = "Orleans";
    private const int ORLEANS_DEFAULT_RU_THROUGHPUT = 400;

    /// <summary>
    /// Tries to create the database and container used for clustering if it does not exist.
    /// </summary>
    public bool IsResourceCreationEnabled { get; set; }

    /// <summary>
    /// Database configured throughput, if set to 0 it will not be configured and container throughput must be set. See https://docs.microsoft.com/en-us/azure/cosmos-db/set-throughput
    /// </summary>
    public int DatabaseThroughput { get; set; } = ORLEANS_DEFAULT_RU_THROUGHPUT;

    /// <summary>
    /// The name of the database to use for clustering information.
    /// </summary>
    public string Database { get; set; } = ORLEANS_DB;

    /// <summary>
    /// The name of the container to use to store clustering information.
    /// </summary>
    public string Container { get; set; } = default!;

    /// <summary>
    /// RU units for container, can be set to 0 if throughput is specified on database level. See https://docs.microsoft.com/en-us/azure/cosmos-db/set-throughput
    /// </summary>
    public int ContainerThroughput { get; set; } = ORLEANS_DEFAULT_RU_THROUGHPUT;

    /// <summary>
    /// Delete the database on initialization.  Useful for testing scenarios.
    /// </summary>
    public bool CleanResourcesOnInitialization { get; set; }

    /// <summary>
    /// The throughput mode to use for the container used for clustering.
    /// </summary>
    /// <remarks>If the throughput mode is set to <see cref="AzureCosmosThroughputMode.Autoscale"/>, the <see cref="ContainerThroughput"/> will need to be at least <code>4000</code> RU.</remarks>
    public AzureCosmosThroughputMode ThroughputMode { get; set; } = AzureCosmosThroughputMode.Manual;

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
    /// The options passed to the Cosmos DB client, or <see langword="null"/> to use default options.
    /// </summary>
    public CosmosClientOptions? ClientOptions { get; set; }

    /// <summary>
    /// Factory method for creating a <see cref="CosmosClient"/>.
    /// </summary>
    internal Func<IServiceProvider, ValueTask<CosmosClient>> CreateClient { get; private set; } = null!;

    internal ThroughputProperties? GetThroughputProperties() =>
        ThroughputMode switch
        {
            AzureCosmosThroughputMode.Manual => ThroughputProperties.CreateManualThroughput(ContainerThroughput),
            AzureCosmosThroughputMode.Autoscale => ThroughputProperties.CreateAutoscaleThroughput(
                ContainerThroughput == 400 ? 4000 : ContainerThroughput),
            AzureCosmosThroughputMode.Serverless => null,
            _ => throw new ArgumentOutOfRangeException(nameof(ThroughputMode), $"There is no setup for throughput mode {ThroughputMode}")
        };

    /// <summary>
    /// Creates a default <see cref="CosmosClientOptions"/> instance.
    /// </summary>
    /// <param name="serviceProvider">The service provider</param>
    /// <returns>
    /// A default <see cref="CosmosClientOptions"/> instance.
    /// </returns>
    internal CosmosClientOptions CreateDefaultOptions(IServiceProvider serviceProvider)
    {
        var clusterOptions = serviceProvider.GetRequiredService<IOptions<ClusterOptions>>().Value;
        var jsonSerializerOptions = new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() },
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            WriteIndented = false,
        };

        var clientOptions = new CosmosClientOptions
        {
            ConnectionMode = ConnectionMode.Direct,
            ApplicationName = clusterOptions.ServiceId,
            Serializer = new AzureCosmosSystemTextJsonSerializer(jsonSerializerOptions)
        };

        return clientOptions;
    }
}
