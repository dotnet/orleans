using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans;
using Presence.Grains;

namespace Presence.PlayerWatcher
{
    public class Program
    {
        public static async Task Main()
        {
            var program = new Program();
            await program.StartAsync();

            Console.CancelKeyPress += async (sender, eargs) =>
            {
                eargs.Cancel = true;
                await program.StopAsync();
            };

            await program.Stopped;
        }

        private readonly IClusterClient client;
        private readonly ILogger<Program> logger;
        private readonly TaskCompletionSource<bool> stoppedSource = new TaskCompletionSource<bool>();

        public Program()
        {
            client = new ClientBuilder()
                .UseLocalhostClustering()
                .ConfigureLogging(_ =>
                {
                    _.AddConsole();
                })
                .ConfigureServices(_ =>
                {
                    _.AddTransient<IGameObserver, LoggerGameObserver>();
                })
                .Build();

            logger = client.ServiceProvider.GetService<ILogger<Program>>();
        }

        public async Task StartAsync()
        {
            var attempt = 0;
            var maxAttempts = 100;
            var delay = TimeSpan.FromSeconds(1);
            await client.Connect(async error =>
            {
                if (++attempt < maxAttempts)
                {
                    logger.LogWarning(error,
                        "Failed to connect to Orleans cluster on attempt {@Attempt} of {@MaxAttempts}.",
                        attempt, maxAttempts);

                    await Task.Delay(delay);
                    return true;
                }
                else
                {
                    logger.LogError(error,
                        "Failed to connect to Orleans cluster on attempt {@Attempt} of {@MaxAttempts}.",
                        attempt, maxAttempts);

                    return false;
                }
            });

            // observing a hardcoded player id for sample purposes
            // the load generator will update player ids within a range that includes this player
            var playerId = new Guid("{2349992C-860A-4EDA-9590-000000000006}");
            var player = client.GetGrain<IPlayerGrain>(playerId);
            IGameGrain game = null;

            // poll for this player to join a game
            while (game == null)
            {
                logger.LogInformation("Getting current game for player {@PlayerId}...",
                    playerId);

                try
                {
                    game = await player.GetCurrentGameAsync();
                }
                catch (Exception error)
                {
                    logger.LogError(error,
                        "Error while requesting current game for player {@PlayerId}",
                        playerId);
                }

                if (game == null)
                {
                    await Task.Delay(1000);
                }
            }

            logger.LogInformation("Observing updates for game {@GameKey}",
                game.GetPrimaryKey());

            // Subscribe for updates
            var watcher = client.ServiceProvider.GetService<IGameObserver>();
            await game.SubscribeForGameUpdatesAsync(
                await client.CreateObjectReference<IGameObserver>(watcher));

            logger.LogInformation("Subscribed successfully to game {@GameKey}",
                game.GetPrimaryKey());
        }

        public async Task StopAsync()
        {
            await client.Close();
            stoppedSource.TrySetResult(true);
            await stoppedSource.Task;
        }

        public Task Stopped => stoppedSource.Task;
    }
}
