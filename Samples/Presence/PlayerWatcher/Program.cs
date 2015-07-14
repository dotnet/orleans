/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Orleans;
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
                GrainClient.Initialize("DevTestClientConfiguration.xml");

                // Hardcoded player ID
                Guid playerId = new Guid("{2349992C-860A-4EDA-9590-000000000006}");
                IPlayerGrain player = PlayerGrainFactory.GetGrain(playerId);
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
                game.SubscribeForGameUpdates(GameObserverFactory.CreateObjectReference(watcher).Result).Wait();

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
