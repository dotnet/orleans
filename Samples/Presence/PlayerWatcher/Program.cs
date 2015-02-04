//*********************************************************//
//    Copyright (c) Microsoft. All rights reserved.
//    
//    Apache 2.0 License
//    
//    You may obtain a copy of the License at
//    http://www.apache.org/licenses/LICENSE-2.0
//    
//    Unless required by applicable law or agreed to in writing, software 
//    distributed under the License is distributed on an "AS IS" BASIS, 
//    WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or 
//    implied. See the License for the specific language governing 
//    permissions and limitations under the License.
//
//*********************************************************

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
                GrainClient.Initialize();

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
