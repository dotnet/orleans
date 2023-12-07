using System.Net;
using Azure;
using Azure.Core;

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
    /// Tries to create the database and container used for clustering if it does not exist. Defaults to <see langword="false"/>.
    /// </summary>
    public bool IsResourceCreationEnabled { get; set; }

    /// <summary>
    /// Database configured throughput. If set to <see langword="null"/>, which is the default value, it will not be configured. 
    /// </summary>
    /// <seealso href="https://learn.microsoft.com/azure/cosmos-db/set-throughput"/>
    public int? DatabaseThroughput { get; set; }

    /// <summary>
    /// The name of the database to use for clustering information. Defaults to <c>Orleans</c>.
    /// </summary>
    public string DatabaseName { get; set; } = "Orleans";

    /// <summary>
    /// The name of the container to use to store clustering information.
    /// </summary>
    public string ContainerName { get; set; } = default!;

    /// <summary>
    /// Throughput properties for containers. The default value is <see langword="null"/>, which indicates that the serverless throughput mode will be used.
    /// </summary>
    /// <seealso href="https://learn.microsoft.com/azure/cosmos-db/set-throughput"/>
    public ThroughputProperties? ContainerThroughputProperties { get; set; }

    /// <summary>
    /// Delete the database on initialization. Intended only for testing scenarios.
    /// </summary>
    /// <remarks>This is only intended for use in testing scenarios.</remarks>
    public bool CleanResourcesOnInitialization { get; set; }

    /// <summary>
    /// The options passed to the Cosmos DB client.
    /// </summary>
    public CosmosClientOptions ClientOptions { get; set; } = new();

    /// <summary>
    /// The operation executor used to execute operations using the Cosmos DB client.
    /// </summary>
    public ICosmosOperationExecutor OperationExecutor { get; set; } = DefaultCosmosOperationExecutor.Instance;

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
    /// <param name="accountEndpoint">The account endpoint. In the form of <code>https://{databaseaccount}.documents.azure.com:443/</code>, <see href="https://learn.microsoft.com/rest/api/cosmos-db/cosmosdb-resource-uri-syntax-for-rest"/></param>
    /// <param name="authKeyOrResourceTokenCredential"><see cref="AzureKeyCredential"/> with master-key or resource token.</param>
    /// <see cref="CosmosClient(string, AzureKeyCredential, CosmosClientOptions)"/>
    public void ConfigureCosmosClient(string accountEndpoint, AzureKeyCredential authKeyOrResourceTokenCredential)
    {
        CreateClient = _ => new(new CosmosClient(accountEndpoint, authKeyOrResourceTokenCredential, ClientOptions));
    }

    /// <summary>
    /// Configures the Cosmos DB client.
    /// </summary>
    /// <param name="accountEndpoint">The account endpoint. In the form of <code>https://{databaseaccount}.documents.azure.com:443/</code>, <see href="https://learn.microsoft.com/rest/api/cosmos-db/cosmosdb-resource-uri-syntax-for-rest"/></param>
    /// <param name="tokenCredential">The token to provide AAD for authorization.</param>
    /// <see cref="CosmosClient(string, TokenCredential, CosmosClientOptions)"/>
    public void ConfigureCosmosClient(string accountEndpoint, TokenCredential tokenCredential)
    {
        CreateClient = _ => new(new CosmosClient(accountEndpoint, tokenCredential, ClientOptions));
    }

    /// <summary>
    /// Configures the Cosmos DB client.
    /// </summary>
    /// <param name="accountEndpoint">The account endpoint. In the form of <code>https://{databaseaccount}.documents.azure.com:443/</code>, <see href="https://learn.microsoft.com/rest/api/cosmos-db/cosmosdb-resource-uri-syntax-for-rest"/></param>
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

/// <summary>
/// Functionality for executing operations using the Cosmos DB client.
/// </summary>
public interface ICosmosOperationExecutor
{
    /// <summary>
    /// Executes the provided Cosmos DB operation.
    /// </summary>
    /// <typeparam name="TArg">The function argument.</typeparam>
    /// <typeparam name="TResult">The result value.</typeparam>
    /// <param name="func">The delegate to execute.</param>
    /// <param name="arg">The argument to pass to delegate invocations.</param>
    /// <returns>The result of invoking the delegate.</returns>
    Task<TResult> ExecuteOperation<TArg, TResult>(Func<TArg, Task<TResult>> func, TArg arg);
}

internal sealed class DefaultCosmosOperationExecutor : ICosmosOperationExecutor
{
    public static readonly DefaultCosmosOperationExecutor Instance = new();
    private const HttpStatusCode TOO_MANY_REQUESTS = (HttpStatusCode)429;
    public async Task<TResult> ExecuteOperation<TArg, TResult>(Func<TArg, Task<TResult>> func, TArg arg)
    {
        // From:  https://blogs.msdn.microsoft.com/bigdatasupport/2015/09/02/dealing-with-requestratetoolarge-errors-in-azure-documentdb-and-testing-performance/
        while (true)
        {
            TimeSpan sleepTime;
            try
            {
                return await func(arg).ConfigureAwait(false);
            }
            catch (CosmosException dce) when (dce.StatusCode == TOO_MANY_REQUESTS)
            {
                sleepTime = dce.RetryAfter ?? TimeSpan.Zero;
            }
            catch (AggregateException ae) when (ae.InnerException is CosmosException dce && dce.StatusCode == TOO_MANY_REQUESTS)
            {
                sleepTime = dce.RetryAfter ?? TimeSpan.Zero;
            }

            await Task.Delay(sleepTime);
        }
    }
}
