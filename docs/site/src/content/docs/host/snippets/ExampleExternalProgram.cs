using Azure.Data.Tables;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Orleans.Configuration;

namespace Client;

internal class ExampleExternalProgram
{
    internal static async Task RunWatcherAsync(string[] args)
    {
        // <program>
        try
        {
            using IHost host = Host.CreateDefaultBuilder(args)
                .UseOrleansClient((context, client) =>
                {
                    client.Configure<ClusterOptions>(options =>
                    {
                        options.ClusterId = "my-first-cluster";
                        options.ServiceId = "MyOrleansService";
                    })
                    .UseAzureStorageClustering(
                        options => options.TableServiceClient = new TableServiceClient(
                            context.Configuration["ORLEANS_AZURE_STORAGE_CONNECTION_STRING"]));
                })
                .UseConsoleLifetime()
                .Build();

            await host.StartAsync();

            IGrainFactory client = host.Services.GetRequiredService<IGrainFactory>();

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
            await game.ObserveGameUpdates(
                client.CreateObjectReference<IGameObserver>(watcher));

            Console.WriteLine(
                "Subscribed successfully. Press <Enter> to stop.");
        }
        catch (Exception e)
        {
            Console.WriteLine(
                $"Unexpected Error: {e.GetBaseException()}");
        }
        // </program>
    }
}

// <gameobserver>
/// <summary>
/// Observer class that implements the observer interface.
/// Need to pass a grain reference to an instance of
/// this class to subscribe for updates.
/// </summary>
class GameObserver : IGameObserver
{
    public void UpdateGameScore(string score)
    {
        Console.WriteLine($"New game score: {score}");
    }
}
// </gameobserver>

// <igameobserver>
public interface IGameObserver : IGrainObserver
{
    void UpdateGameScore(string score);
}
// </igameobserver>
