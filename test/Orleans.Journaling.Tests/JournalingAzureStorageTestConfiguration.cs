using TestExtensions;
using Xunit;

namespace Orleans.Journaling.Tests;

internal static class JournalingAzureStorageTestConfiguration
{
    public static void CheckPreconditionsOrThrow()
    {
        if (TestDefaultConfiguration.UseAadAuthentication)
        {
            Skip.If(!TestDefaultConfiguration.GetValue(nameof(TestDefaultConfiguration.DataBlobUri), out _), "DataBlobUri is not set. Skipping test.");
        }
        else
        {
            _ = TestDefaultConfiguration.AzureStorageConnectionString;
        }
    }

    public static AzureBlobJournalStorageOptions ConfigureTestDefaults(this AzureBlobJournalStorageOptions options)
    {
        if (TestDefaultConfiguration.UseAadAuthentication)
        {
            options.ConfigureBlobServiceClient(TestDefaultConfiguration.DataBlobUri, TestDefaultConfiguration.TokenCredential);
        }
        else
        {
            options.ConfigureBlobServiceClient(TestDefaultConfiguration.AzureStorageConnectionString);
        }

        return options;
    }

    public static AzureTableJournalStorageOptions ConfigureTestDefaults(this AzureTableJournalStorageOptions options)
    {
        if (TestDefaultConfiguration.UseAadAuthentication)
        {
            options.ConfigureTableServiceClient(TestDefaultConfiguration.TableEndpoint, TestDefaultConfiguration.TokenCredential);
        }
        else
        {
            options.ConfigureTableServiceClient(TestDefaultConfiguration.AzureStorageConnectionString);
        }

        return options;
    }
}
