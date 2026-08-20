using TestExtensions;

namespace Tester.Cosmos;

public class CosmosTestUtils
{
    public static bool IsCosmosEmulator
    {
        get
        {
            var endpoint = TestDefaultConfiguration.CosmosDBAccountEndpoint;
            return Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.IsLoopback;
        }
    }

    public static void CheckCosmosStorage()
    {
        if (string.IsNullOrWhiteSpace(TestDefaultConfiguration.CosmosDBAccountEndpoint)
            || string.IsNullOrWhiteSpace(TestDefaultConfiguration.CosmosDBAccountKey))
        {
            throw Xunit.Sdk.SkipException.ForSkip("Cosmos DB is not configured");
        }
    }

    public static void SkipIfCosmosEmulator(string reason)
    {
        CheckCosmosStorage();

        if (IsCosmosEmulator)
        {
            throw Xunit.Sdk.SkipException.ForSkip(reason);
        }
    }
}
