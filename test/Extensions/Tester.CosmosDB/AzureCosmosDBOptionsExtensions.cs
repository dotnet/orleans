using Azure.Identity;
using TestExtensions;

namespace Tester.AzureUtils;

public static class AzureCosmosDBOptionsExtensions
{
    public static Orleans.Clustering.CosmosDB.AzureCosmosDBOptions ConfigureTestDefaults(this Orleans.Clustering.CosmosDB.AzureCosmosDBOptions options)
    {
        if (TestDefaultConfiguration.UseAadAuthentication)
        {
            options.Credential = new DefaultAzureCredential();
        }
        else
        {
            options.AccountKey = TestDefaultConfiguration.CosmosDBAccountKey;
        }

        options.AccountEndpoint = TestDefaultConfiguration.CosmosDBAccountEndpoint;
        options.IsResourceCreationEnabled = true;
        options.CleanResourcesOnInitialization = true;

        return options;
    }

    public static Orleans.Persistence.CosmosDB.AzureCosmosDBOptions ConfigureTestDefaults(this Orleans.Persistence.CosmosDB.AzureCosmosDBOptions options)
    {
        if (TestDefaultConfiguration.UseAadAuthentication)
        {
            options.Credential = new DefaultAzureCredential();
        }
        else
        {
            options.AccountKey = TestDefaultConfiguration.CosmosDBAccountKey;
        }

        options.AccountEndpoint = TestDefaultConfiguration.CosmosDBAccountEndpoint;
        options.IsResourceCreationEnabled = true;

        return options;
    }

    public static Orleans.Reminders.CosmosDB.AzureCosmosDBOptions ConfigureTestDefaults(this Orleans.Reminders.CosmosDB.AzureCosmosDBOptions options)
    {
        if (TestDefaultConfiguration.UseAadAuthentication)
        {
            options.Credential = new DefaultAzureCredential();
        }
        else
        {
            options.AccountKey = TestDefaultConfiguration.CosmosDBAccountKey;
        }

        options.AccountEndpoint = TestDefaultConfiguration.CosmosDBAccountEndpoint;
        options.IsResourceCreationEnabled = true;
        options.CleanResourcesOnInitialization = true;

        return options;
    }
}