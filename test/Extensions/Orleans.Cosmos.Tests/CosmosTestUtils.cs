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
            throw new SkipException();
        }
    }

    public static void SkipIfCosmosEmulator(string reason)
    {
        CheckCosmosStorage();

        if (IsCosmosEmulator)
        {
            throw new SkipException(reason);
        }
    }
}
