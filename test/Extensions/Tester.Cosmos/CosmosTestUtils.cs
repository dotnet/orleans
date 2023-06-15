using TestExtensions;

namespace Tester.Cosmos;

public class CosmosTestUtils
{
    public static void CheckCosmosStorage()
    {
        if (string.IsNullOrWhiteSpace(TestDefaultConfiguration.CosmosDBAccountEndpoint)
            || string.IsNullOrWhiteSpace(TestDefaultConfiguration.CosmosDBAccountKey))
        {
            throw new SkipException();
        }
    }
}
