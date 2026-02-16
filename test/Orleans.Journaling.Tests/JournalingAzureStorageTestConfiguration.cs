using TestExtensions;
using Xunit;

namespace Orleans.Journaling.Tests;

internal static class JournalingAzureStorageTestConfiguration
{
    public static void CheckPreconditionsOrThrow()
    {
        Skip.If(string.IsNullOrEmpty(TestDefaultConfiguration.DataConnectionString), "DataConnectionString is not set. Skipping test.");
    }

    public static AzureAppendBlobStateMachineStorageOptions ConfigureTestDefaults(this AzureAppendBlobStateMachineStorageOptions options)
    {
        options.ConfigureBlobServiceClient(TestDefaultConfiguration.DataConnectionString);

        return options;
    }
}
