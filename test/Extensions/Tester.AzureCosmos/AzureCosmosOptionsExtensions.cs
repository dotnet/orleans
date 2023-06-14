using Azure.Identity;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using TestExtensions;

namespace Tester.AzureCosmos;

public static class AzureCosmosOptionsExtensions
{
    public static void ConfigureTestDefaults(this OptionsBuilder<Orleans.Clustering.AzureCosmos.AzureCosmosOptions> optionsBuilder)
    {
        optionsBuilder.Configure((Orleans.Clustering.AzureCosmos.AzureCosmosOptions options, IOptions<ClusterOptions> clusterOptions) =>
        {
            if (TestDefaultConfiguration.UseAadAuthentication)
            {
                options.ConfigureCosmosClient(TestDefaultConfiguration.CosmosDBAccountEndpoint, new DefaultAzureCredential());
            }
            else
            {
                options.ConfigureCosmosClient(GetCosmosClientUsingAccountKey());
            }

            options.IsResourceCreationEnabled = true;
        });
    }

    public static void ConfigureTestDefaults(this OptionsBuilder<Orleans.Persistence.AzureCosmos.AzureCosmosOptions> optionsBuilder)
    {
        optionsBuilder.Configure((Orleans.Persistence.AzureCosmos.AzureCosmosOptions options, IOptions<ClusterOptions> clusterOptions) =>
        {
            if (TestDefaultConfiguration.UseAadAuthentication)
            {
                options.ConfigureCosmosClient(TestDefaultConfiguration.CosmosDBAccountEndpoint, new DefaultAzureCredential());
            }
            else
            {
                options.ConfigureCosmosClient(GetCosmosClientUsingAccountKey());
            }

            options.IsResourceCreationEnabled = true;
        });
    }

    public static void ConfigureTestDefaults(this OptionsBuilder<Orleans.Reminders.AzureCosmos.AzureCosmosOptions> optionsBuilder)
    {
        optionsBuilder.Configure((Orleans.Reminders.AzureCosmos.AzureCosmosOptions options, IOptions<ClusterOptions> clusterOptions) =>
        {
            if (TestDefaultConfiguration.UseAadAuthentication)
            {
                options.ConfigureCosmosClient(TestDefaultConfiguration.CosmosDBAccountEndpoint, new DefaultAzureCredential());
            }
            else
            {
                options.ConfigureCosmosClient(GetCosmosClientUsingAccountKey());
            }

            options.IsResourceCreationEnabled = true;
        });
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