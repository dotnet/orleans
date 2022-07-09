using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

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

internal class AzureCosmosDBConnectionFactory
{
    public static ValueTask<CosmosClient> CreateCosmosClient(IServiceProvider serviceProvider, AzureCosmosDBOptions options)
    {
        if (options.CosmosDBClientFactory is not null)
        {
            return options.CosmosDBClientFactory(serviceProvider);
        }

        var clusterOptions = serviceProvider.GetRequiredService<IOptions<ClusterOptions>>().Value;

        var jsonSerializerOptions = options.SerializerOptions ?? new JsonSerializerOptions
        {
            Converters = { new JsonStringEnumConverter() },
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            WriteIndented = false,
        };

        var cosmosOptions = new CosmosClientOptions
        {
            ConnectionMode = options.ConnectionMode,
            ApplicationName = clusterOptions.ClusterId,
            Serializer = new AzureCosmosDBSTJSerializer(jsonSerializerOptions)
        };

        var cosmos = options.Credential is not null
            ? new CosmosClient(options.AccountEndpoint, options.Credential, cosmosOptions)
            : new CosmosClient(options.AccountEndpoint, options.AccountKey, cosmosOptions);

        return ValueTask.FromResult(cosmos);
    }
}