using Microsoft.Azure.Cosmos;
using Orleans.Journaling.Cosmos;
using TestExtensions;
using Xunit;

namespace Orleans.Journaling.Tests;

internal static class JournalingAzureStorageTestConfiguration
{
    public static void CheckPreconditionsOrThrow()
    {
        if (TestDefaultConfiguration.UseAadAuthentication)
        {
            Skip.If(string.IsNullOrEmpty(TestDefaultConfiguration.DataBlobUri.ToString()), "DataBlobUri is not set. Skipping test.");
        }
        else
        {
            Skip.If(string.IsNullOrEmpty(TestDefaultConfiguration.DataConnectionString), "DataConnectionString is not set. Skipping test.");
        }
    }

    public static AzureAppendBlobStateMachineStorageOptions ConfigureTestDefaults(this AzureAppendBlobStateMachineStorageOptions options)
    {
        if (TestDefaultConfiguration.UseAadAuthentication)
        {
            options.BlobServiceClient = new(TestDefaultConfiguration.DataBlobUri, TestDefaultConfiguration.TokenCredential);
        }
        else
        {
            options.BlobServiceClient = new(TestDefaultConfiguration.DataConnectionString);
        }

        return options;
    }
}

internal static class JournalingCosmosTestConfiguration
{
    public static void CheckPreconditionsOrThrow()
    {
        if (string.IsNullOrWhiteSpace(TestDefaultConfiguration.CosmosDBAccountEndpoint) ||
            string.IsNullOrWhiteSpace(TestDefaultConfiguration.CosmosDBAccountKey))
        {
            throw new SkipException();
        }
    }

    public static void ConfigureTestDefaults(this CosmosLogStorageOptions options)
    {
        if (TestDefaultConfiguration.UseAadAuthentication)
        {
            options.ConfigureCosmosClient(TestDefaultConfiguration.CosmosDBAccountEndpoint, TestDefaultConfiguration.TokenCredential);
        }
        else
        {
            options.ConfigureCosmosClient(GetCosmosClientUsingAccountKey());
        }

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
