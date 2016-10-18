using System;
using System.Threading;
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
            try
            {
                var config = ClientConfiguration.LocalhostSilo();
                GrainClient.Initialize(config);

                // Hardcoded player ID
                Guid playerId = new Guid("{2349992C-860A-4EDA-9590-000000000006}");
                IPlayerGrain player = GrainClient.GrainFactory.GetGrain<IPlayerGrain>(playerId);
                IGameGrain game = null;

                while (game == null)
                {
                    Console.WriteLine("Getting current game for player {0}...", playerId);

                    try
                    {
                        game = player.GetCurrentGame().Result;
                        if (game == null) // Wait until the player joins a game
                        {
                            game = null;
                            Thread.Sleep(5000);
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
                game.SubscribeForGameUpdates(GrainClient.GrainFactory.CreateObjectReference<IGameObserver>(watcher).Result).Wait();

                // Block main thread so that the process doesn't exit. Updates arrive on thread pool threads.
                Console.WriteLine("Subscribed successfully. Press <Enter> to stop.");
                Console.ReadLine();
            }
            catch (Exception exc)
            {
                Console.WriteLine("Unexpected Error: {0}", exc.GetBaseException());
            }
        }

        /// <summary>
        /// Observer class that implements the observer interface. Need to pass a grain reference to an instance of this class to subscribe for updates.
        /// </summary>
        private class GameObserver : IGameObserver
        {
            // Receive updates
            public void UpdateGameScore(string score)
            {
                Console.WriteLine("New game score: {0}", score);
            }
        }
    }
}
