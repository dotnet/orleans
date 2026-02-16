using Microsoft.Azure.Cosmos;
using Orleans.Clustering.Cosmos;
using Orleans.Persistence.Cosmos;
using Orleans.Reminders.Cosmos;
using TestExtensions;

namespace Tester.Cosmos;

public static class CosmosOptionsExtensions
{
    public static void ConfigureTestDefaults(this CosmosClusteringOptions options)
    {
        options.ConfigureCosmosClient(GetCosmosClientUsingAccountKey());
        options.IsResourceCreationEnabled = true;
    }

    public static void ConfigureTestDefaults(this CosmosGrainStorageOptions options)
    {
        options.ConfigureCosmosClient(GetCosmosClientUsingAccountKey());
        options.IsResourceCreationEnabled = true;
    }

    public static void ConfigureTestDefaults(this CosmosReminderTableOptions options)
    {
        options.ConfigureCosmosClient(GetCosmosClientUsingAccountKey());
        options.IsResourceCreationEnabled = true;
    }

    private static Func<IServiceProvider, ValueTask<CosmosClient>> GetCosmosClientUsingAccountKey()
    {
        return _ =>
        {
            var cosmosClientOptions = new CosmosClientOptions()
            {
                HttpClientFactory = () =>
                {
                    HttpMessageHandler httpMessageHandler = new HttpClientHandler()
                    {
                        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                    };

                    return new HttpClient(httpMessageHandler);
                },

                ConnectionMode = ConnectionMode.Gateway
            };

            return new(new CosmosClient(TestDefaultConfiguration.CosmosDBAccountEndpoint, TestDefaultConfiguration.CosmosDBAccountKey, cosmosClientOptions));
        };
    }
}
