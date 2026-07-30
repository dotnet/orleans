using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;

namespace ClientSnippets;

// <external_client_example>
internal static class ExternalClientExample
{
    private static string connectionString = "UseDevelopmentStorage=true";

    public static async Task RunWatcherAsync()
    {
        try
        {
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

                // Hardcoded player ID
                Guid playerId = new("{2349992C-860A-4EDA-9590-000000000006}");
                IPlayerGrain player = client.GetGrain<IPlayerGrain>(playerId);
                IGameGrain? game = null;
                while (game is null)
                {
                    Console.WriteLine(
                        $"Getting current game for player {playerId}...");

                    try
                    {
                        game = await player.GetCurrentGame();
                        if (game is null) // Wait until the player joins a game
                        {
                            await Task.Delay(TimeSpan.FromMilliseconds(5_000));
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Exception: {ex.GetBaseException()}");
                    }
                }

                Console.WriteLine(
                    $"Subscribing to updates for game {game.GetPrimaryKey()}...");

                // Subscribe for updates
                var watcher = new GameObserver();
                await game.SubscribeForGameUpdates(
                    await client.CreateObjectReference<IGameObserver>(watcher));

                Console.WriteLine(
                    "Subscribed successfully. Press <Enter> to stop.");

                Console.ReadLine();
            }
            catch (Exception e)
            {
                Console.WriteLine(
                    $"Unexpected Error: {e.GetBaseException()}");
            }
        }
    }

/// <summary>
/// Observer class that implements the observer interface.
/// Need to pass a grain reference to an instance of
/// this class to subscribe for updates.
/// </summary>
class GameObserver : IGameObserver
{
    public void UpdateGameScore(string score)
    {
        Console.WriteLine("New game score: {0}", score);
    }
}
// </external_client_example>
