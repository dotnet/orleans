using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans;
using Presence.Grains;
using Presence.Grains.Models;

namespace Presence.LoadGenerator
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            Console.Title = nameof(LoadGenerator);

            // wire-up graceful termination in response to Ctrl+C
            var cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (sender, eargs) =>
            {
                eargs.Cancel = true;
                cancellation.Cancel();
            };

            try
            {
                await RunAsync(args, cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                // supress any cancellation exceptions
            }
        }

        private static async Task RunAsync(string[] args, CancellationToken token)
        {
            // build the orleans client
            var client = new ClientBuilder()
                .UseLocalhostClustering()
                .ConfigureLogging(_ =>
                {
                    _.AddConsole();
                })
                .Build();

            // keep a logger for general use
            var logger = client.ServiceProvider.GetService<ILogger<Program>>();

            // connect to the orleans cluster
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

                    await Task.Delay(delay, token);

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

            // number of games to simulate
            var nGames = 10;

            // number of players in each game
            var nPlayersPerGame = 4;

            // interval for sending updates
            var sendInterval = TimeSpan.FromSeconds(2);

            // number of updates to send
            var nIterations = 100;

            // Precreate base heartbeat data objects for each of the games.
            // We'll modify them before every time before sending.
            var heartbeats = new HeartbeatData[nGames];
            for (var i = 0; i < nGames; i++)
            {
                heartbeats[i] = new HeartbeatData(
                    Guid.NewGuid(),
                    new GameStatus(
                        Enumerable.Range(0, nPlayersPerGame).Select(j => GetPlayerId(i * nPlayersPerGame + j)).ToImmutableHashSet(),
                        string.Empty));
            }

            var iteration = 0;

            // PresenceGrain is a StatelessWorker, so we use a single grain ID for auto-scale
            var presence = client.GetGrain<IPresenceGrain>(0);
            var promises = new Task[nGames];

            while (++iteration < nIterations)
            {
                logger.LogInformation("Sending heartbeat series #{@Iteration}", iteration);

                try
                {
                    for (var i = 0; i < nGames; i++)
                    {
                        // Simultate a meaningful game score
                        heartbeats[i] = heartbeats[i].WithNewScore($"{iteration}:{(iteration > 5 ? iteration - 5 : 0)}");

                        // We serialize the HeartbeatData object to a byte[] only to simulate the real life scenario where data comes in
                        // as a binary blob and requires an initial processing before it can be routed to the proper destination.
                        // We could have sent the HeartbeatData object directly to the game grain because we know the game ID.
                        // For the sake of simulation we just pretend we don't.
                        promises[i] = presence.HeartbeatAsync(HeartbeatDataDotNetSerializer.Serialize(heartbeats[i]));
                    }

                    // Wait for all calls to finish.
                    await Task.WhenAll(promises);
                }
                catch (Exception error)
                {
                    logger.LogError(error, "Error while sending hearbeats to Orleans cluster");
                }

                await Task.Delay(sendInterval, token);
            }
        }

        /// <summary>
        /// Generates GUIDs for player IDs.
        /// </summary>
        private static Guid GetPlayerId(int playerIndex)
        {
            // For convenience, we generate a set of predefined subsequent GUIDs for players using this one as a base.
            return Guid.ParseExact($"2349992C-860A-4EDA-9590-{playerIndex:D12}", "D");
        }
    }
}
