using Azure.Identity;
using Microsoft.Azure.Cosmos;
using TestExtensions;

namespace Tester.AzureCosmos;

public static class AzureCosmosOptionsExtensions
{
    public static Orleans.Clustering.AzureCosmos.AzureCosmosOptions ConfigureTestDefaults(this Orleans.Clustering.AzureCosmos.AzureCosmosOptions options)
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
        options.DatabaseThroughput = 10000;

        return options;
    }

    public static Orleans.Persistence.AzureCosmos.AzureCosmosOptions ConfigureTestDefaults(this Orleans.Persistence.AzureCosmos.AzureCosmosOptions options)
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
        options.DatabaseThroughput = 10000;

        return options;
    }

    public static Orleans.Reminders.AzureCosmos.AzureCosmosOptions ConfigureTestDefaults(this Orleans.Reminders.AzureCosmos.AzureCosmosOptions options)
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
        options.DatabaseThroughput = 10000;

        return options;
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