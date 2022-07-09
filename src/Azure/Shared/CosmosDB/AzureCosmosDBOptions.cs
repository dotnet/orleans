using System.Text.Json;
using Azure.Core;

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

public abstract class AzureCosmosDBOptions
{
    private const string ORLEANS_DB = "Orleans";
    private const int ORLEANS_DEFAULT_RU_THROUGHPUT = 400;

    /// <summary>
    /// Gets or sets the cosmos account endpoint URI. This can be retrieved from the Overview section of the Azure Portal.
    /// This is required if you are authenticating using tokens.
    /// <remarks>
    /// In the form of https://{databaseaccount}.documents.azure.com:443/, see: https://docs.microsoft.com/en-us/rest/api/cosmos-db/cosmosdb-resource-uri-syntax-for-rest
    /// </remarks>
    /// </summary>
    public string AccountEndpoint { get; set; } = default!;

    /// <summary>
    /// The account key used for a cosmos DB account.
    /// </summary>
    [Redact]
    public string? AccountKey { get; set; }

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
    /// The connection mode to use when connecting to the azure cosmos DB service.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ConnectionMode ConnectionMode { get; set; } = ConnectionMode.Direct;

    /// <summary>
    /// Delete the database on initialization.  Useful for testing scenarios.
    /// </summary>
    public bool CleanResourcesOnInitialization { get; set; }

    /// <summary>
    /// The throughput mode to use for the container used for clustering.
    /// </summary>
    /// <remarks>If the throughput mode is set to Autoscale then the <see cref="ContainerThroughput"/> will need to be at least 4000 RUs.</remarks>
    public AzureCosmosDbThroughputMode ThroughputMode { get; set; } = AzureCosmosDbThroughputMode.Manual;

    /// <summary>
    /// The Azure Active Directory credentials.
    /// </summary>
    /// <remarks>If this is set then the <see cref="AccountKey"/> will be ignored.</remarks>
    public TokenCredential? Credential { get; set; }

    /// <summary>
    /// Json Serializer options.
    /// </summary>
    public JsonSerializerOptions? SerializerOptions { get; set; }

    /// <summary>
    /// Factory method for creating a <see cref="CosmosClient"/>.
    /// </summary>
    /// <remarks>If this method is specified, the <see cref="AccountEndpoint"/> and <see cref="AccountKey"/> will be ignored.</remarks>
    public Func<IServiceProvider, ValueTask<CosmosClient>>? CosmosDBClientFactory { get; set; }

    // TODO: Consistency level for emulator (defaults to Session; https://docs.microsoft.com/en-us/azure/cosmos-db/local-emulator)
    internal IndexingMode? GetConsistencyLevel() => !string.IsNullOrWhiteSpace(this.AccountEndpoint) && this.AccountEndpoint.Contains("localhost") ? IndexingMode.None : null;

    internal ThroughputProperties? GetThroughputProperties() =>
        this.ThroughputMode switch
        {
            AzureCosmosDbThroughputMode.Manual => ThroughputProperties.CreateManualThroughput(this.ContainerThroughput),
            AzureCosmosDbThroughputMode.Autoscale => ThroughputProperties.CreateAutoscaleThroughput(
                this.ContainerThroughput == 400 ? 4000 : this.ContainerThroughput),
            AzureCosmosDbThroughputMode.Serverless => null,
            _ => throw new ArgumentOutOfRangeException(nameof(this.ThroughputMode), $"There is no setup for throughput mode {this.ThroughputMode}")
        };
}
