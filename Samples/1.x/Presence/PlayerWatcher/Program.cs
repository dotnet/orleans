using System;
using System.Threading;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime.Configuration;
using Orleans.Samples.Presence.GrainInterfaces;

namespace PlayerWatcher
{
    class Program
    {
        /// <summary>
        /// Simulates a companion application that connects to the game that a particular player is currently part of
        /// and subscribes to receive live notifications about its progress.
        /// </summary>
        static void Main(string[] args)
        {
            var client = RunWatcher().Result;

            // Block main thread so that the process doesn't exit.
            // Updates arrive on thread pool threads.
            Console.ReadLine();

            // Close connection to the cluster.
            client.Close().Wait();
        }

        static async Task<IClusterClient> RunWatcher()
        {
            try

            {
                // Connect to local silo
                var config = ClientConfiguration.LocalhostSilo();
                var client = new ClientBuilder().UseConfiguration(config).Build();
                await client.Connect();

                // Hardcoded player ID
                Guid playerId = new Guid("{2349992C-860A-4EDA-9590-000000000006}");
                IPlayerGrain player = client.GetGrain<IPlayerGrain>(playerId);
                IGameGrain game = null;

                while (game == null)
                {
                    Console.WriteLine("Getting current game for player {0}...", playerId);

                    try
                    {
                        game = await player.GetCurrentGame();
                        if (game == null) // Wait until the player joins a game
                        {
                            await Task.Delay(5000);
                        }
                    }
                    catch (Exception exc)
                    {
                        Console.WriteLine("Exception: ", exc.GetBaseException());
                    }
                }

                Console.WriteLine("Subscribing to updates for game {0}...", game.GetPrimaryKey());

                // Subscribe for updates
                var watcher = new GameObserver();
                await game.SubscribeForGameUpdates(
                    await client.CreateObjectReference<IGameObserver>(watcher));

                Console.WriteLine("Subscribed successfully. Press <Enter> to stop.");

                return client;
            }
            catch (Exception exc)
            {
                Console.WriteLine("Unexpected Error: {0}", exc.GetBaseException());
                throw;
            }
        }
    }

    /// <summary>
    /// Observer class that implements the observer interface. Need to pass a grain reference to an instance of this class to subscribe for updates.
    /// </summary>
    class GameObserver : IGameObserver
    {
        // Receive updates
        public void UpdateGameScore(string score)
        {
            Console.WriteLine("New game score: {0}", score);
        }
    }
}
