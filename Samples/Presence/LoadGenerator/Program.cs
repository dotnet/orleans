using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime.Configuration;
using Orleans.Samples.Presence.GrainInterfaces;

namespace LoadGenerator
{
    class Program
    {
        /// <summary>
        /// Simulates periodic updates for a bunch of games similar to what game consoles of mobile apps do.
        /// </summary>
        static void Main(string[] args)
        {
            var client = RunLoadGenerator().Result;

            // Block main thread so that the process doesn't exit.
            // Updates arrive on thread pool threads.
            Console.ReadLine();

            // Close connection to the cluster.
            client.Close().Wait();
        }

        static async Task<IClusterClient> RunLoadGenerator()
        {
            try
            {
                // Connect to local silo
                var config = ClientConfiguration.LocalhostSilo();
                var client = new ClientBuilder().UseConfiguration(config).Build();
                await client.Connect();

                int nGames = 10; // number of games to simulate
                int nPlayersPerGame = 4; // number of players in each game
                TimeSpan sendInterval = TimeSpan.FromSeconds(2); // interval for sending updates
                int nIterations = 100;

                // Precreate base heartbeat data objects for each of the games.
                // We'll modify them before every time before sending.
                HeartbeatData[] heartbeats = new HeartbeatData[nGames];
                for (int i = 0; i < nGames; i++)
                {
                    heartbeats[i] = new HeartbeatData();
                    heartbeats[i].Game = Guid.NewGuid();
                    for (int j = 0; j < nPlayersPerGame; j++)
                    {
                        heartbeats[i].Status.Players.Add(GetPlayerId(i*nPlayersPerGame + j));
                    }
                }

                int iteration = 0;
                IPresenceGrain presence = client.GetGrain<IPresenceGrain>(0); // PresenceGrain is a StatelessWorker, so we use a single grain ID for auto-scale
                List<Task> promises = new List<Task>();

                while (iteration++ < nIterations)
                {
                    Console.WriteLine("Sending heartbeat series #{0}", iteration);

                    promises.Clear();

                    try
                    {
                        for (int i = 0; i < nGames; i++)
                        {
                            heartbeats[i].Status.Score = String.Format("{0}:{1}", iteration, iteration > 5 ? iteration - 5 : 0); // Simultate a meaningful game score

                            // We serialize the HeartbeatData object to a byte[] only to simulate the real life scenario where data comes in
                            // as a binary blob and requires an initial processing before it can be routed to the proper destination.
                            // We could have sent the HeartbeatData object directly to the game grain because we know the game ID.
                            // For the sake of simulation we just pretend we don't.
                            Task t = presence.Heartbeat(HeartbeatDataDotNetSerializer.Serialize(heartbeats[i]));

                            promises.Add(t);
                        }

                        // Wait for all calls to finish.
                        // It is okay to block the thread here because it's a client program with no parallelism.
                        // One should never block a thread in grain code.
                        Task.WaitAll(promises.ToArray());
                    }
                    catch (Exception exc)
                    {
                        Console.WriteLine("Exception: {0}", exc.GetBaseException());
                    }

                    Thread.Sleep(sendInterval);
                }

                return client;
            }
            catch (Exception exc)
            {
                Console.WriteLine("Unexpected Error: {0}", exc.GetBaseException());
                throw;
            }
        }

        /// <summary>
        /// Generates GUIDs for player IDs
        /// </summary>
        private static Guid GetPlayerId(int playerIndex)
        {
            // For convenience, we generate a set of predefined subsequent GUIDs for players using this one as a base.
            byte[] playerGuid = new Guid("{2349992C-860A-4EDA-9590-000000000000}").ToByteArray();
            playerGuid[15] = (byte) (playerGuid[15] + playerIndex);
            return new Guid(playerGuid);
        }
    }
}
