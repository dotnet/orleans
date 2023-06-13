using TestExtensions;

namespace Tester.AzureCosmos;

public class AzureCosmosTestUtils
{
    public static void CheckCosmosDbStorage()
    {
        if (string.IsNullOrWhiteSpace(TestDefaultConfiguration.CosmosDBAccountEndpoint)
            || string.IsNullOrWhiteSpace(TestDefaultConfiguration.CosmosDBAccountKey))
        {
            throw new SkipException();
        }
    }
}
