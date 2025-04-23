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
