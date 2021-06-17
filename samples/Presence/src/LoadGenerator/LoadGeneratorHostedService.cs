using System;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Presence.Grains;
using Presence.Grains.Models;

namespace Presence.LoadGenerator
{
    public class LoadGeneratorHostedService : BackgroundService
    {
        private readonly ILogger<LoadGeneratorHostedService> _logger;
        private readonly IClusterClient _client;

        public LoadGeneratorHostedService(ILogger<LoadGeneratorHostedService> logger, IClusterClient client)
        {
            _logger = logger;
            _client = client;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
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
            var presence = _client.GetGrain<IPresenceGrain>(0);
            var promises = new Task[nGames];

            while (!stoppingToken.IsCancellationRequested && ++iteration < nIterations)
            {
                _logger.LogInformation("Sending heartbeat series #{@Iteration}", iteration);

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
                    _logger.LogError(error, "Error while sending hearbeats to Orleans cluster");
                }

                try
                {
                    await Task.Delay(sendInterval, stoppingToken);
                }
                catch when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
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
