using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Presence.Grains;

namespace Presence.PlayerWatcher
{
    public class Program
    {
        public static Task Main()
        {
            Console.Title = nameof(PlayerWatcher);

            return new HostBuilder()
                .ConfigureServices(services =>
                {
                    // add regular services
                    services.AddTransient<IGameObserver, LoggerGameObserver>();

                    // this hosted service connects and disconnects from the cluster along with the host
                    // it also provides the cluster client to other services that request it
                    services.AddSingleton<ClusterClientHostedService>();
                    services.AddSingleton<IHostedService>(_ => _.GetService<ClusterClientHostedService>());
                    services.AddSingleton(_ => _.GetService<ClusterClientHostedService>().Client);
                })
                .ConfigureLogging(builder =>
                {
                    builder.AddConsole();
                })
                .RunConsoleAsync();
        }

        private readonly IClusterClient client;
        private readonly ILogger<Program> logger;
        private readonly TaskCompletionSource<bool> stoppedSource = new TaskCompletionSource<bool>();
        private readonly CancellationTokenSource startCancellation = new CancellationTokenSource();

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


            await StartWatcherAsync();
        }

        private async Task StartWatcherAsync()
        {
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
                    try
                    {
                        await Task.Delay(1000, startCancellation.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }

            logger.LogInformation("Observing updates for game {@GameKey}",
                game.GetPrimaryKey());

            // subscribe for updates
            var watcher = client.ServiceProvider.GetService<IGameObserver>();
            await game.ObserveGameUpdatesAsync(
                await client.CreateObjectReference<IGameObserver>(watcher));

            logger.LogInformation("Subscribed successfully to game {@GameKey}",
                game.GetPrimaryKey());
        }

        public async Task StopAsync()
        {
            startCancellation.Cancel();
            try
            {
                await client.Close();
            }
            catch (Exception error)
            {
                logger.LogError(error, "Error while gracefully disconnecting from Orleans cluster.");
            }
            stoppedSource.TrySetResult(true);
            await stoppedSource.Task;
        }

        public Task Stopped => stoppedSource.Task;
    }
}
