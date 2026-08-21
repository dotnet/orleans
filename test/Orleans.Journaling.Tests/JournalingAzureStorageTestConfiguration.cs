using TestExtensions;
using Tester;

namespace Orleans.Journaling.Tests;

internal static class JournalingAzureStorageTestConfiguration
{
    public static void CheckPreconditionsOrThrow()
    {
        TestUtils.CheckForAzureStorage();
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
