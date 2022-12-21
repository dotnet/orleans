using Azure.Identity;
using Microsoft.Extensions.Options;
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
            options.ConfigureCosmosClient(TestDefaultConfiguration.CosmosDBAccountEndpoint, TestDefaultConfiguration.CosmosDBAccountKey);
        }

        options.IsResourceCreationEnabled = true;
        options.DatabaseThroughput = 4000;

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
            options.ConfigureCosmosClient(TestDefaultConfiguration.CosmosDBAccountEndpoint, TestDefaultConfiguration.CosmosDBAccountKey);
        }

        options.IsResourceCreationEnabled = true;
        options.DatabaseThroughput = 4000;

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
            options.ConfigureCosmosClient(TestDefaultConfiguration.CosmosDBAccountEndpoint, TestDefaultConfiguration.CosmosDBAccountKey);
        }

        options.IsResourceCreationEnabled = true;
        options.DatabaseThroughput = 4000;

        return options;
    }
}