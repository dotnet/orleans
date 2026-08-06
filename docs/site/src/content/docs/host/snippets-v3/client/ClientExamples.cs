using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;

namespace ClientSnippets;

// Stub interfaces for compilation
public interface IValueGrain : IGrainWithGuidKey { }
public interface IPlayerGrain : IGrainWithGuidKey
{
    Task<IGameGrain?> GetCurrentGame();
}
public interface IGameGrain : IGrainWithGuidKey
{
    Task SubscribeForGameUpdates(IGameObserver observer);
}
public interface IGameObserver : IGrainObserver
{
    void UpdateGameScore(string score);
}

internal static class ClientExamples
{
    private static string connectionString = "UseDevelopmentStorage=true";

    public static void ConfigureClient()
    {
        // <client_config>
        var client = new ClientBuilder()
            .Configure<ClusterOptions>(options =>
            {
                options.ClusterId = "my-first-cluster";
                options.ServiceId = "MyOrleansService";
            })
            .UseAzureStorageClustering(
                options => options.ConfigureTableServiceClient(connectionString))
            .ConfigureApplicationParts(
                parts => parts.AddApplicationPart(typeof(IValueGrain).Assembly))
            .Build();
        // </client_config>
    }

    public static async Task ConnectClient(IClusterClient client)
    {
        // <client_connect>
        await client.Connect();
        // </client_connect>
    }

    public static void ConfigureGatewayOptions()
    {
        // <gateway_options>
        var client = new ClientBuilder()
            // ...
            .Configure<GatewayOptions>(
                options =>                         // Default is 1 min.
                options.GatewayListRefreshPeriod = TimeSpan.FromMinutes(10))
            .Build();
        // </gateway_options>
    }
}
